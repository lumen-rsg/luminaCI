using System.Diagnostics;
using System.Security.Cryptography;
using Lumina.SourceService.Data;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.SourceService.Services;

/// <summary>
/// Handles multi-protocol source fetching: git, tar, http, ftp, rsync, svn, hg, local.
/// Downloads sources to a staging directory, then uploads to MinIO for persistence.
/// </summary>
public class SourceFetchService
{
    private readonly SourceDbContext _db;
    private readonly SourceStorageService _storage;
    private readonly ILogger<SourceFetchService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SourceUriValidator _uriValidator;
    private readonly string _tempDir;
    private readonly string _sourcesDir;
    private readonly int _maxRetries;
    private readonly int _timeoutMinutes;

    public SourceFetchService(
        SourceDbContext db,
        SourceStorageService storage,
        ILogger<SourceFetchService> logger,
        IConfiguration config,
        IServiceScopeFactory scopeFactory,
        SourceUriValidator uriValidator)
    {
        _db = db;
        _storage = storage;
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;
        _uriValidator = uriValidator;
        _tempDir = config["Source:TempDir"] ?? "/tmp/source-fetch";
        _sourcesDir = config["Source:SourcesDir"] ?? "/opt/lumina/sources";
        _maxRetries = int.TryParse(config["Source:MaxRetries"] ?? "3", out var r) ? r : 3;
        _timeoutMinutes = int.TryParse(config["Source:FetchTimeoutMinutes"] ?? "30", out var t) ? t : 30;
    }

    /// <summary>
    /// Fetch sources for a specific package. Creates a SourceJob record and runs the fetch.
    /// </summary>
    public async Task<SourceJob> FetchAsync(string packageName, string sourceUrl, SourceType sourceType,
        string? branch = null, int maxRetries = 0)
    {
        var effectiveRetries = maxRetries > 0 ? maxRetries : _maxRetries;

        // Create or update the source job
        var existingJob = await _db.SourceJobs
            .Where(j => j.PackageName == packageName)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync();

        var job = existingJob ?? new Shared.Models.SourceJob
        {
            Id = Guid.NewGuid(),
            PackageName = packageName,
            CreatedAt = DateTime.UtcNow
        };

        job.SourceUrl = sourceUrl;
        job.SourceType = sourceType;
        job.SourceBranch = branch;
        job.Status = SourceStatus.Fetching;
        job.FetchStartedAt = DateTime.UtcNow;
        job.FetchCompletedAt = null;
        job.ErrorMessage = null;
        job.RetryCount = 0;
        job.UpdatedAt = DateTime.UtcNow;

        if (existingJob == null)
            _db.SourceJobs.Add(job);
        else
            _db.SourceJobs.Update(job);

        await _db.SaveChangesAsync();

        // Run the fetch in background
        _ = ExecuteFetchAsync(job, effectiveRetries);

        return job;
    }

    /// <summary>
    /// Fetch all packages from config
    /// </summary>
    public async Task<List<SourceJob>> FetchAllAsync(ConfigParserService configParser, int maxRetries = 0)
    {
        var packages = configParser.ParsePackages();
        var jobs = new List<SourceJob>();

        foreach (var pkg in packages)
        {
            try
            {
                var job = await FetchAsync(pkg.Name, pkg.Source, pkg.SourceType, pkg.SourceBranch, maxRetries);
                jobs.Add(job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start fetch for package {Name}", pkg.Name);
            }
        }

        return jobs;
    }

    private async Task ExecuteFetchAsync(Shared.Models.SourceJob job, int maxRetries)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation("Fetching source for {Package} (attempt {Attempt}/{Max})",
                    job.PackageName, attempt + 1, maxRetries + 1);

                var stagingDir = Path.Combine(_tempDir, job.PackageName, job.Id.ToString("N"));
                Directory.CreateDirectory(stagingDir);

                var result = await FetchByProtocolAsync(job.SourceUrl, job.SourceType, job.SourceBranch, stagingDir);

                // Create tarball from fetched content if it's a directory (git, svn, hg)
                string archivePath;
                if (result.IsDirectory)
                {
                    archivePath = await CreateTarballAsync(result.Path, stagingDir, job.PackageName);
                }
                else
                {
                    archivePath = result.Path;
                }

                // Compute hash
                var hash = await ComputeSha256Async(archivePath);
                var fileSize = new FileInfo(archivePath).Length;

                // Upload to MinIO
                var storagePath = await _storage.UploadAsync(job.PackageName, archivePath);

                // Update job as successful — use fresh scope for background task
                using var updateScope = _scopeFactory.CreateScope();
                var updateDb = updateScope.ServiceProvider.GetRequiredService<SourceDbContext>();
                var updateJob = await updateDb.SourceJobs.FindAsync(job.Id);

                if (updateJob != null)
                {
                    updateJob.Status = SourceStatus.Ready;
                    updateJob.StoragePath = storagePath;
                    updateJob.FileSize = fileSize;
                    updateJob.HashSha256 = hash;
                    updateJob.FetchCompletedAt = DateTime.UtcNow;
                    updateJob.UpdatedAt = DateTime.UtcNow;
                    updateJob.RetryCount = attempt;
                    updateDb.SourceJobs.Update(updateJob);
                    await updateDb.SaveChangesAsync();
                }

                _logger.LogInformation(
                    "Source fetch completed for {Package}: {Size} bytes, hash={Hash}",
                    job.PackageName, fileSize, hash[..16] + "...");

                // Cleanup staging
                try { Directory.Delete(stagingDir, true); } catch { }

                return;
            }
            catch (Exception ex)
            {
                job.RetryCount = attempt;
                job.ErrorMessage = ex.Message;
                _logger.LogWarning(ex,
                    "Fetch attempt {Attempt} failed for {Package}: {Error}",
                    attempt + 1, job.PackageName, ex.Message);

                if (attempt < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // exponential backoff
                    await Task.Delay(delay);
                }
            }
        }

        // All retries exhausted
        job.Status = SourceStatus.Failed;
        job.FetchCompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        _db.SourceJobs.Update(job);
        await _db.SaveChangesAsync();

        _logger.LogError("Source fetch failed for {Package} after {Attempts} attempts",
            job.PackageName, maxRetries + 1);
    }

    private async Task<FetchResult> FetchByProtocolAsync(
        string sourceUrl, SourceType sourceType, string? branch, string stagingDir)
    {
        // SECURITY: single choke point for SSRF / arbitrary-file-read defense.
        // conf.ini is user-writable, so sourceUrl/sourceType/branch are treated
        // as untrusted here even though callers also validate. The validator
        // canonicalizes the URL (and, for local sources, confines the path to
        // the trusted root) before anything reaches a process or the filesystem.
        var validated = await _uriValidator.ValidateAsync(sourceUrl, sourceType, branch);
        var safeUrl = validated.LocalPath ?? validated.Url;
        var safeBranch = validated.Branch;

        return sourceType switch
        {
            SourceType.Git => await FetchGitAsync(safeUrl, safeBranch ?? "main", stagingDir),
            SourceType.Tar or SourceType.Http or SourceType.Ftp
                => await FetchHttpAsync(safeUrl, stagingDir),
            SourceType.Rsync => await FetchRsyncAsync(safeUrl, stagingDir),
            SourceType.Svn => await FetchSvnAsync(safeUrl, safeBranch, stagingDir),
            SourceType.Hg => await FetchHgAsync(safeUrl, safeBranch, stagingDir),
            SourceType.Local => await FetchLocalAsync(safeUrl, stagingDir),
            _ => throw new NotSupportedException($"Source type {sourceType} is not supported")
        };
    }

    // ─── Protocol implementations ───

    private async Task<FetchResult> FetchGitAsync(string url, string branch, string stagingDir)
    {
        var cloneDir = Path.Combine(stagingDir, "repo");

        _logger.LogInformation("git clone {Url} (branch: {Branch})", url, branch);

        // SECURITY: build argv via ArgumentList — never interpolate url/branch
        // into a shell string. The trailing "--" prevents url/branch from being
        // parsed as options if they start with "-".
        var result = await RunCommandAsync("git",
            new[] { "clone", "--depth", "50", "--branch", branch, "--", url, cloneDir },
            stagingDir, _timeoutMinutes);

        if (result.ExitCode != 0)
            throw new Exception($"git clone failed (exit {result.ExitCode}): {result.Error}");

        // Initialize submodules if present
        if (File.Exists(Path.Combine(cloneDir, ".gitmodules")))
        {
            await RunCommandAsync("git",
                new[] { "submodule", "update", "--init", "--recursive" },
                cloneDir, _timeoutMinutes);
        }

        return new FetchResult(cloneDir, true);
    }

    private async Task<FetchResult> FetchHttpAsync(string url, string stagingDir)
    {
        var fileName = GetFileNameFromUrl(url);
        var targetPath = Path.Combine(stagingDir, fileName);

        _logger.LogInformation("Downloading {Url} → {File}", url, fileName);

        // SECURITY: url/targetPath are passed as discrete argv tokens; the URL
        // has already passed SourceUriValidator (scheme/host/private-range).
        // -L (follow redirects) is kept because legitimate upstreams (GitHub
        // release assets, CDNs) redirect; the residual SSRF surface from a
        // redirect to a private range is bounded by configuring a host
        // allow-list (Source:AllowedHosts) for the initial resolution.
        var result = await RunCommandAsync("curl",
            new[] { "-L", "-f", "-o", targetPath, "--", url },
            stagingDir, _timeoutMinutes);

        if (result.ExitCode != 0)
        {
            _logger.LogInformation("curl failed, trying wget...");
            result = await RunCommandAsync("wget",
                new[] { "-O", targetPath, "--", url },
                stagingDir, _timeoutMinutes);

            if (result.ExitCode != 0)
                throw new Exception($"Download failed: curl/wget both failed for {url}");
        }

        if (!File.Exists(targetPath))
            throw new Exception($"Downloaded file not found: {targetPath}");

        // Check if it's a tarball that needs extraction
        if (IsTarball(fileName))
        {
            var extractDir = Path.Combine(stagingDir, "extracted");
            Directory.CreateDirectory(extractDir);
            await ExtractTarballAsync(targetPath, extractDir);
            return new FetchResult(extractDir, true);
        }

        return new FetchResult(targetPath, false);
    }

    private async Task<FetchResult> FetchRsyncAsync(string url, string stagingDir)
    {
        _logger.LogInformation("rsync {Url}", url);

        var result = await RunCommandAsync("rsync",
            new[] { "-az", $"--timeout={_timeoutMinutes * 60}", "--", url, $"{stagingDir}/" },
            stagingDir, _timeoutMinutes);

        if (result.ExitCode != 0)
            throw new Exception($"rsync failed (exit {result.ExitCode}): {result.Error}");

        return new FetchResult(stagingDir, true);
    }

    private async Task<FetchResult> FetchSvnAsync(string url, string? branch, string stagingDir)
    {
        var checkoutDir = Path.Combine(stagingDir, "checkout");

        // If branch specified, append to URL. branch is already validated to
        // contain no shell metacharacters and is composed into the URL before
        // the validator-style argv passing — the resulting svnUrl is a single
        // argv token so no injection is possible.
        var svnUrl = url;
        if (!string.IsNullOrWhiteSpace(branch))
        {
            svnUrl = $"{url}/branches/{branch}";
        }

        _logger.LogInformation("svn checkout {Url}", svnUrl);

        var result = await RunCommandAsync("svn",
            new[] { "checkout", "--non-interactive", svnUrl, checkoutDir },
            stagingDir, _timeoutMinutes);

        if (result.ExitCode != 0)
            throw new Exception($"svn checkout failed (exit {result.ExitCode}): {result.Error}");

        return new FetchResult(checkoutDir, true);
    }

    private async Task<FetchResult> FetchHgAsync(string url, string? branch, string stagingDir)
    {
        var cloneDir = Path.Combine(stagingDir, "repo");

        _logger.LogInformation("hg clone {Url}", url);

        // SECURITY: argv tokens; branch is a discrete "-b" + value pair.
        var args = new List<string> { "clone" };
        if (!string.IsNullOrWhiteSpace(branch))
        {
            args.Add("-b");
            args.Add(branch);
        }
        args.Add(url);
        args.Add(cloneDir);

        var result = await RunCommandAsync("hg", args, stagingDir, _timeoutMinutes);

        if (result.ExitCode != 0)
            throw new Exception($"hg clone failed (exit {result.ExitCode}): {result.Error}");

        return new FetchResult(cloneDir, true);
    }

    private async Task<FetchResult> FetchLocalAsync(string path, string stagingDir)
    {
        _logger.LogInformation("Copying local source from {Path}", path);

        if (!Directory.Exists(path) && !File.Exists(path))
            throw new Exception($"Local source path not found: {path}");

        if (File.Exists(path))
        {
            var destPath = Path.Combine(stagingDir, Path.GetFileName(path));
            File.Copy(path, destPath, true);
            return new FetchResult(destPath, false);
        }

        // Copy directory
        foreach (var dir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(path, stagingDir));
        }

        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            var destFile = file.Replace(path, stagingDir);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(file, destFile, true);
        }

        return new FetchResult(stagingDir, true);
    }

    // ─── Helpers ───

    private async Task<string> CreateTarballAsync(string sourceDir, string stagingDir, string packageName)
    {
        var tarballPath = Path.Combine(stagingDir, $"{packageName}-sources.tar.gz");

        // If sourceDir contains a single directory or file, tar that content directly
        var entries = Directory.GetFileSystemEntries(sourceDir);
        string tarSource;

        if (entries.Length == 1)
        {
            tarSource = entries[0];
        }
        else
        {
            // Wrap in a named directory
            var wrapDir = Path.Combine(stagingDir, $"{packageName}-wrap");
            Directory.CreateDirectory(wrapDir);
            foreach (var entry in entries)
            {
                var dest = Path.Combine(wrapDir, Path.GetFileName(entry));
                if (Directory.Exists(entry))
                    CopyDirectory(entry, dest);
                else
                    File.Copy(entry, dest, true);
            }
            tarSource = wrapDir;
        }

        var result = await RunCommandAsync("tar",
            new[] { "-czf", tarballPath, "-C", Path.GetDirectoryName(tarSource)!, Path.GetFileName(tarSource) },
            stagingDir, _timeoutMinutes);

        if (result.ExitCode != 0)
            throw new Exception($"Failed to create tarball: {result.Error}");

        return tarballPath;
    }

    private async Task ExtractTarballAsync(string tarballPath, string targetDir)
    {
        var result = await RunCommandAsync("tar",
            new[] { "-xf", tarballPath, "-C", targetDir },
            Path.GetDirectoryName(tarballPath)!, _timeoutMinutes);

        if (result.ExitCode != 0)
            throw new Exception($"Failed to extract tarball: {result.Error}");
    }

    private static bool IsTarball(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        return lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz") ||
               lower.EndsWith(".tar.bz2") || lower.EndsWith(".tbz2") ||
               lower.EndsWith(".tar.xz") || lower.EndsWith(".txz") ||
               lower.EndsWith(".tar");
    }

    private static string GetFileNameFromUrl(string url)
    {
        var uri = new Uri(url);
        var segments = uri.Segments;
        var fileName = segments[^1].TrimEnd('/', '\\');
        return string.IsNullOrWhiteSpace(fileName) ? "source-archive" : fileName;
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<CommandResult> RunCommandAsync(
        string command, IReadOnlyList<string> arguments, string workingDir, int timeoutMinutes)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));

        // SECURITY: Use ArgumentList (one token per element) instead of a single
        // Arguments string. With UseShellExecute=false there is no shell, but
        // ArgumentList additionally guarantees that no caller-supplied value
        // (URL, branch, path) is ever re-parsed as multiple tokens — mirroring
        // the PgpSigningService pattern. No string interpolation reaches here.
        var psi = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        await process.WaitForExitAsync(cts.Token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new CommandResult(process.ExitCode, stdout, stderr);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    /// <summary>
    /// Fetch sources and prepare a tarball for RPM build.
    /// Creates {name}-{version}.tar.gz with {name}-{version}/ directory inside.
    /// Saves to shared volume for BuildService to pick up.
    /// </summary>
    public async Task<SourcePrepareResult> FetchAndPrepareForBuildAsync(
        string packageName, string sourceUrl, SourceType sourceType, string? branch,
        string packageVersion, string specContent)
    {
        var stagingDir = Path.Combine(_tempDir, packageName, "build-prep");
        if (Directory.Exists(stagingDir))
            try { Directory.Delete(stagingDir, true); } catch { }
        Directory.CreateDirectory(stagingDir);

        _logger.LogInformation("Fetching and preparing sources for {Package} v{Version}", packageName, packageVersion);

        // 1. Fetch sources
        var result = await FetchByProtocolAsync(sourceUrl, sourceType, branch, stagingDir);

        // 2. Determine the source directory
        string sourceContentDir;
        if (result.IsDirectory)
        {
            sourceContentDir = result.Path;
        }
        else
        {
            // Single file — wrap in a directory
            var wrapDir = Path.Combine(stagingDir, "content");
            Directory.CreateDirectory(wrapDir);
            File.Copy(result.Path, Path.Combine(wrapDir, Path.GetFileName(result.Path)), true);
            sourceContentDir = wrapDir;
        }

        // 3. Create tarball with {name}-{version}/ directory structure
        var tarballName = $"{packageName}-{packageVersion}.tar.gz";
        var sharedDir = Path.Combine(_sourcesDir, packageName);
        Directory.CreateDirectory(sharedDir);

        var tarballPath = Path.Combine(sharedDir, tarballName);

        // Create the tarball with the correct directory name inside
        var tmpTarDir = Path.Combine(stagingDir, "tardir");
        var namedDir = Path.Combine(tmpTarDir, $"{packageName}-{packageVersion}");
        Directory.CreateDirectory(namedDir);

        // Copy all content from source into the named directory
        foreach (var entry in Directory.GetFileSystemEntries(sourceContentDir))
        {
            var dest = Path.Combine(namedDir, Path.GetFileName(entry));
            if (Directory.Exists(entry))
                CopyDirectory(entry, dest);
            else
                File.Copy(entry, dest, true);
        }

        // Create tarball
        var tarResult = await RunCommandAsync("tar",
            new[] { "-czf", tarballPath, "-C", tmpTarDir, $"{packageName}-{packageVersion}" },
            stagingDir, _timeoutMinutes);

        if (tarResult.ExitCode != 0)
            throw new Exception($"Failed to create source tarball: {tarResult.Error}");

        // 4. Save spec file alongside
        var specPath = Path.Combine(sharedDir, $"{packageName}.spec");
        await File.WriteAllTextAsync(specPath, specContent);

        // 5. Cleanup staging
        try { Directory.Delete(stagingDir, true); } catch { }

        _logger.LogInformation("Source tarball prepared: {Tarball} ({Size} bytes)",
            tarballPath, new FileInfo(tarballPath).Length);

        return new SourcePrepareResult(sharedDir, tarballPath, specPath);
    }

    public record SourcePrepareResult(string SourceDir, string TarballPath, string SpecPath);
    private record FetchResult(string Path, bool IsDirectory);
    private record CommandResult(int ExitCode, string Output, string Error);
}
