using System.Diagnostics;
using System.Security.Cryptography;
using Lumina.SourceService.Data;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.SourceService.Services;

/// <summary>
/// Fetches pinned HTTPS Git/archive sources (or explicitly enabled confined
/// local sources), then stores a verified content-addressed artifact.
/// </summary>
public class SourceFetchService
{
    private readonly SourceDbContext _db;
    private readonly SourceStorageService _storage;
    private readonly ILogger<SourceFetchService> _logger;
    private readonly SourceUriValidator _uriValidator;
    private readonly SourceIntegrityService _integrity;
    private readonly string _tempDir;
    private readonly string _sourcesDir;
    private readonly int _timeoutMinutes;
    private readonly long _maxStagingBytes;
    private readonly int _maxStagingEntries;

    public SourceFetchService(
        SourceDbContext db,
        SourceStorageService storage,
        ILogger<SourceFetchService> logger,
        IConfiguration config,
        SourceUriValidator uriValidator,
        SourceIntegrityService integrity)
    {
        _db = db;
        _storage = storage;
        _logger = logger;
        _uriValidator = uriValidator;
        _integrity = integrity;
        _tempDir = config["Source:TempDir"] ?? "/tmp/source-fetch";
        _sourcesDir = config["Source:SourcesDir"] ?? "/opt/lumina/sources";
        _timeoutMinutes = int.TryParse(config["Source:FetchTimeoutMinutes"] ?? "30", out var t) ? t : 30;
        _maxStagingBytes = Math.Clamp(
            config.GetValue("Source:MaxStagingBytes", 4_294_967_296L),
            1_048_576L, 42_949_672_960L);
        _maxStagingEntries = Math.Clamp(
            config.GetValue("Source:MaxFileCount", 100_000) * 2,
            2, 2_000_000);
    }

    /// <summary>Executes a job already leased by <see cref="SourceFetchWorker"/>.</summary>
    public async Task ExecuteFetchAsync(
        Guid jobId,
        string leaseOwner,
        CancellationToken cancellationToken)
    {
        var job = await _db.SourceJobs.SingleOrDefaultAsync(
            j => j.Id == jobId &&
                 j.Status == SourceStatus.Fetching &&
                 j.LeaseOwner == leaseOwner,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Source job {jobId} is not leased by worker {leaseOwner}.");
        var stagingDir = Path.Combine(
            _tempDir, job.PackageName, job.Id.ToString("N"), leaseOwner);

        try
        {
            for (var attempt = job.RetryCount; attempt <= job.MaxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _db.Entry(job).ReloadAsync(cancellationToken);
                if (job.CancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                ResetStagingDirectory(stagingDir);
                job.RetryCount = attempt;
                job.ErrorMessage = null;
                job.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);

                try
                {
                    _logger.LogInformation("Fetching source for {Package} (attempt {Attempt}/{Max})",
                        job.PackageName, attempt + 1, job.MaxRetries + 1);

                    var result = await FetchByProtocolAsync(
                        job.SourceUrl, job.SourceType, job.SourceBranch, job.ExpectedSha256,
                        stagingDir, cancellationToken);

                    string archivePath;
                    if (result.IsDirectory)
                    {
                        _integrity.ValidateTree(result.Path);
                        archivePath = await CreateTarballAsync(
                            result.Path, stagingDir, job.PackageName, cancellationToken);
                    }
                    else
                    {
                        archivePath = result.Path;
                    }

                    var hash = await ComputeSha256Async(archivePath, cancellationToken);
                    var fileSize = new FileInfo(archivePath).Length;
                    cancellationToken.ThrowIfCancellationRequested();
                    var storagePath = await _storage.UploadAsync(
                        job.PackageName, archivePath, hash, cancellationToken);

                    job.Status = SourceStatus.Ready;
                    job.StoragePath = storagePath;
                    job.FileSize = fileSize;
                    job.HashSha256 = hash;
                    job.ResolvedRevision = result.ResolvedRevision;
                    job.ResolvedUrl = result.ResolvedUrl;
                    job.FetchCompletedAt = DateTime.UtcNow;
                    job.UpdatedAt = DateTime.UtcNow;
                    job.LeaseOwner = null;
                    job.LeaseExpiresAt = null;
                    job.HeartbeatAt = null;
                    await _db.SaveChangesAsync(cancellationToken);

                    _logger.LogInformation(
                        "Source fetch completed for {Package}: {Size} bytes, hash={Hash}",
                        job.PackageName, fileSize, hash[..16] + "...");
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Another replica reclaimed the expired lease. Its claim is
                    // authoritative; this stale executor must not update state.
                    throw;
                }
                catch (Exception ex)
                {
                    job.RetryCount = attempt < job.MaxRetries ? attempt + 1 : attempt;
                    job.ErrorMessage = ex.Message;
                    job.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(cancellationToken);
                    _logger.LogWarning(ex,
                        "Fetch attempt {Attempt} failed for {Package}: {Error}",
                        attempt + 1, job.PackageName, ex.Message);

                    if (attempt < job.MaxRetries)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        await Task.Delay(delay, cancellationToken);
                    }
                }
            }

            job.Status = SourceStatus.Failed;
            job.FetchCompletedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            job.HeartbeatAt = null;
            await _db.SaveChangesAsync(CancellationToken.None);

            _logger.LogError("Source fetch failed for {Package} after {Attempts} attempts",
                job.PackageName, job.MaxRetries + 1);
        }
        catch (OperationCanceledException)
        {
            await _db.Entry(job).ReloadAsync(CancellationToken.None);
            if (job.CancellationRequested)
            {
                job.Status = SourceStatus.Cancelled;
                job.FetchCompletedAt = DateTime.UtcNow;
                job.UpdatedAt = DateTime.UtcNow;
                job.LeaseOwner = null;
                job.LeaseExpiresAt = null;
                job.HeartbeatAt = null;
                job.ErrorMessage = "Cancelled by request.";
                await _db.SaveChangesAsync(CancellationToken.None);
            }
            // Host shutdown leaves the lease in place. Another worker safely
            // reclaims it after expiry and repeats the current attempt.
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogWarning(
                "Worker {WorkerId} lost the lease for source job {JobId}",
                leaseOwner, jobId);
        }
        finally
        {
            TryDeleteStagingDirectory(stagingDir);
        }
    }

    private async Task<FetchResult> FetchByProtocolAsync(
        string sourceUrl,
        SourceType sourceType,
        string? branch,
        string? expectedSha256,
        string stagingDir,
        CancellationToken cancellationToken)
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
            SourceType.Git => await FetchGitAsync(validated, safeBranch ?? "main", stagingDir, cancellationToken),
            SourceType.Tar => await FetchHttpAsync(
                safeUrl, expectedSha256, true, stagingDir, cancellationToken),
            SourceType.Http => await FetchHttpAsync(
                safeUrl, expectedSha256, false, stagingDir, cancellationToken),
            SourceType.Ftp => throw new SourceValidationException(
                "FTP sources are disabled because they cannot provide a pinned, authenticated transport."),
            SourceType.Rsync or SourceType.Svn or SourceType.Hg =>
                throw new SourceValidationException(
                    $"{sourceType} sources are disabled because they cannot provide a pinned, authenticated transport."),
            SourceType.Local => await FetchLocalAsync(safeUrl, stagingDir, cancellationToken),
            _ => throw new NotSupportedException($"Source type {sourceType} is not supported")
        };
    }

    // ─── Protocol implementations ───

    private async Task<FetchResult> FetchGitAsync(
        ValidatedSource source,
        string branch,
        string stagingDir,
        CancellationToken cancellationToken)
    {
        var cloneDir = Path.Combine(stagingDir, "repo");
        var uri = new Uri(source.Url);
        if (uri.Scheme is not "https")
            throw new SourceValidationException("Git sources must use HTTPS.");
        if (!string.IsNullOrEmpty(uri.Query))
            throw new SourceValidationException("Git source URLs must not contain query credentials.");

        _logger.LogInformation(
            "Cloning Git source {Url} (reference: {Reference})",
            SourceIntegrityService.RedactUri(uri), branch);

        // SECURITY: build argv via ArgumentList — never interpolate url/branch
        // into a shell string. The trailing "--" prevents url/branch from being
        // parsed as options if they start with "-".
        var gitArguments = BuildPinnedGitArguments(source);
        gitArguments.AddRange(
            ["clone", "--depth", "50", "--branch", branch, "--", source.Url, cloneDir]);
        var result = await RunCommandAsync("git",
            gitArguments,
            stagingDir, _timeoutMinutes, cancellationToken);

        if (result.ExitCode != 0)
            throw new Exception($"Git clone failed with exit code {result.ExitCode}.");

        if (File.Exists(Path.Combine(cloneDir, ".gitmodules")))
            throw new SourceValidationException(
                "Git submodules are not permitted without an explicit pinned source manifest.");

        var revision = await RunCommandAsync(
            "git", ["rev-parse", "HEAD"], cloneDir, _timeoutMinutes, cancellationToken);
        var commit = revision.Output.Trim();
        if (revision.ExitCode != 0 ||
            commit.Length is not (40 or 64) ||
            commit.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Git did not return a valid resolved commit.");
        }

        Directory.Delete(Path.Combine(cloneDir, ".git"), recursive: true);
        return new FetchResult(
            cloneDir, true, commit.ToLowerInvariant(),
            SourceIntegrityService.RedactUri(uri));
    }

    private async Task<FetchResult> FetchHttpAsync(
        string url,
        string? expectedSha256,
        bool extractArchive,
        string stagingDir,
        CancellationToken cancellationToken)
    {
        var fileName = GetFileNameFromUrl(url);
        var targetPath = Path.Combine(stagingDir, fileName);

        _logger.LogInformation(
            "Downloading source {Url} to {File}",
            SourceIntegrityService.RedactUri(new Uri(url)), fileName);
        var resolvedUrl = await _integrity.DownloadHttpAsync(
            url, targetPath, cancellationToken);

        if (!File.Exists(targetPath))
            throw new Exception($"Downloaded file not found: {targetPath}");

        var downloadedHash = await ComputeSha256Async(targetPath, cancellationToken);
        if (expectedSha256 is not null &&
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(downloadedHash),
                Convert.FromHexString(expectedSha256)))
        {
            throw new SourceValidationException(
                "Downloaded source does not match the expected SHA-256.");
        }

        if (extractArchive)
        {
            var extractDir = Path.Combine(stagingDir, "extracted");
            Directory.CreateDirectory(extractDir);
            await _integrity.ExtractTarAsync(targetPath, extractDir, cancellationToken);
            return new FetchResult(extractDir, true, null, resolvedUrl);
        }

        return new FetchResult(targetPath, false, null, resolvedUrl);
    }

    private static List<string> BuildPinnedGitArguments(ValidatedSource source)
    {
        var uri = new Uri(source.Url);
        var port = uri.IsDefaultPort ? 443 : uri.Port;
        var addresses = string.Join(
            ',', source.Addresses.Select(address =>
                address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? $"[{address}]"
                    : address.ToString()));
        return
        [
            "-c", "http.followRedirects=false",
            "-c", $"http.curloptResolve={uri.Host}:{port}:{addresses}"
        ];
    }

    private async Task<FetchResult> FetchLocalAsync(
        string path, string stagingDir, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Copying local source from {Path}", path);

        if (!Directory.Exists(path) && !File.Exists(path))
            throw new Exception($"Local source path not found: {path}");

        if (File.Exists(path))
        {
            _integrity.ValidateFile(path);
            var destPath = Path.Combine(stagingDir, Path.GetFileName(path));
            File.Copy(path, destPath, true);
            return new FetchResult(destPath, false);
        }

        _integrity.ValidateTree(path);
        // Copy directory
        foreach (var dir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(dir.Replace(path, stagingDir));
        }

        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destFile = file.Replace(path, stagingDir);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(file, destFile, true);
        }

        return new FetchResult(stagingDir, true);
    }

    // ─── Helpers ───

    private async Task<string> CreateTarballAsync(
        string sourceDir, string stagingDir, string packageName, CancellationToken cancellationToken)
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
            stagingDir, _timeoutMinutes, cancellationToken);

        if (result.ExitCode != 0)
            throw new Exception($"Failed to create tarball: {result.Error}");

        return tarballPath;
    }

    private static string GetFileNameFromUrl(string url)
    {
        var uri = new Uri(url);
        var segments = uri.Segments;
        var fileName = segments[^1].TrimEnd('/', '\\');
        return string.IsNullOrWhiteSpace(fileName) ? "source-archive" : fileName;
    }

    private static async Task<string> ComputeSha256Async(
        string filePath, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<CommandResult> RunCommandAsync(
        string command,
        IReadOnlyList<string> arguments,
        string workingDir,
        int timeoutMinutes,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(
            linkedCts.Token);

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

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            var waitTask = process.WaitForExitAsync(linkedCts.Token);
            var monitorTask = MonitorStagingLimitsAsync(workingDir, monitorCts.Token);
            var completed = await Task.WhenAny(waitTask, monitorTask);
            if (completed == monitorTask)
            {
                var violation = await monitorTask;
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                throw new SourceValidationException(violation);
            }

            await waitTask;
            monitorCts.Cancel();
            return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await stdoutTask;
            await stderrTask;
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new TimeoutException(
                $"{command} exceeded the {timeoutMinutes}-minute source fetch timeout.");
        }
        finally
        {
            monitorCts.Cancel();
        }
    }

    private async Task<string> MonitorStagingLimitsAsync(
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var pending = new Stack<DirectoryInfo>();
                pending.Push(new DirectoryInfo(rootDirectory));
                long totalBytes = 0;
                var entries = 0;

                while (pending.TryPop(out var directory))
                {
                    foreach (var entry in directory.EnumerateFileSystemInfos())
                    {
                        entries++;
                        if (entries > _maxStagingEntries)
                            return "Source staging exceeded the maximum file count.";
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;
                        if (entry is DirectoryInfo child)
                            pending.Push(child);
                        else
                            totalBytes = checked(totalBytes + ((FileInfo)entry).Length);
                        if (totalBytes > _maxStagingBytes)
                            return "Source staging exceeded the maximum byte limit.";
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The process may be renaming files while they are enumerated.
                // Retry on the next sampling interval.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    private static void ResetStagingDirectory(string stagingDirectory)
    {
        if (Directory.Exists(stagingDirectory))
            Directory.Delete(stagingDirectory, recursive: true);
        Directory.CreateDirectory(stagingDirectory);
    }

    private void TryDeleteStagingDirectory(string stagingDirectory)
    {
        try
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean source staging directory {Directory}", stagingDirectory);
        }
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
        var result = await FetchByProtocolAsync(
            sourceUrl, sourceType, branch, null, stagingDir, CancellationToken.None);

        // 2. Determine the source directory
        string sourceContentDir;
        if (result.IsDirectory)
        {
            _integrity.ValidateTree(result.Path);
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
            stagingDir, _timeoutMinutes, CancellationToken.None);

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
    private record FetchResult(
        string Path,
        bool IsDirectory,
        string? ResolvedRevision = null,
        string? ResolvedUrl = null);
    private record CommandResult(int ExitCode, string Output, string Error);
}
