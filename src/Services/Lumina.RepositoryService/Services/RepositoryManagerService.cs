using System.Diagnostics;
using System.Security.Cryptography;
using Lumina.Shared.Extensions;

namespace Lumina.RepositoryService.Services;

/// <summary>
/// Manages RPM repository filesystem structure and createrepo_c metadata.
/// </summary>
public class RepositoryManagerService
{
    private readonly ILogger<RepositoryManagerService> _logger;
    private readonly string _reposBasePath;

    public RepositoryManagerService(ILogger<RepositoryManagerService> logger, IConfiguration config)
    {
        _logger = logger;
        _reposBasePath = config["Repository:BasePath"] ?? "/app/repos";
    }

    /// <summary>
    /// Validates basePath/arch against an allow-list before any filesystem or
    /// process use. This is the front-line defense (clear, early rejection);
    /// <see cref="ProcessArgumentSanitizer.ResolveConfinedPath"/> remains the
    /// back-stop confinement check at the FS boundary.
    /// </summary>
    private static void EnsureSafeRepoSegments(string basePath, string? arch)
    {
        ProcessArgumentSanitizer.ValidateRepositoryBasePath(basePath);
        if (arch is not null)
            ProcessArgumentSanitizer.ValidateRepositoryArch(arch);
    }

    /// <summary>
    /// Ensures the repository directory structure exists: {basePath}/{arch}/.
    /// The combined path is confined to <see cref="_reposBasePath"/> so a crafted
    /// basePath/arch cannot create or resolve a directory outside the repo root.
    /// </summary>
    public string EnsureRepoDir(string basePath, string arch)
    {
        EnsureSafeRepoSegments(basePath, arch);
        var archDir = ProcessArgumentSanitizer.ResolveConfinedPath(
            Path.Combine(basePath.Trim('/'), arch), _reposBasePath);
        if (!Directory.Exists(archDir))
        {
            Directory.CreateDirectory(archDir);
            _logger.LogInformation("Created repository directory: {Dir}", archDir);
        }
        return archDir;
    }

    /// <summary>
    /// Saves an uploaded RPM file to the appropriate repository directory.
    /// Returns the full path where the file was saved.
    /// </summary>
    public async Task<string> SaveRpmAsync(string basePath, string arch, string fileName, Stream fileStream)
    {
        var archDir = EnsureRepoDir(basePath, arch);
        var filePath = Path.Combine(archDir, fileName);

        await using var fs = new FileStream(filePath, FileMode.Create);
        await fileStream.CopyToAsync(fs);
        fs.Flush();

        _logger.LogInformation("Saved RPM: {Path} ({Size} bytes)", filePath, fs.Length);
        return filePath;
    }

    /// <summary>
    /// Saves an uploaded RPM file from byte array.
    /// Returns the full path where the file was saved.
    /// </summary>
    public string SaveRpm(string basePath, string arch, string fileName, byte[] data)
    {
        var archDir = EnsureRepoDir(basePath, arch);
        var filePath = Path.Combine(archDir, fileName);
        File.WriteAllBytes(filePath, data);

        _logger.LogInformation("Saved RPM: {Path} ({Size} bytes)", filePath, data.Length);
        return filePath;
    }

    /// <summary>
    /// Computes SHA-256 hash of a file.
    /// </summary>
    public string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Extracts RPM metadata using rpm -qip command.
    /// Returns a dictionary with Name, Version, Release, Arch, Size fields.
    /// </summary>
    public RpmMetadata? ExtractRpmMetadata(string filePath)
    {
        try
        {
            // SECURITY: Confine the client-supplied path to the repository root.
            // The old SanitizeFilePath only blocked "..", so absolute paths like
            // "/etc/passwd" (no traversal segment) passed through. ResolveConfinedPath
            // canonicalizes with Path.GetFullPath and enforces a root prefix.
            var safePath = ProcessArgumentSanitizer.ResolveConfinedPath(filePath, _reposBasePath);

            var startInfo = new ProcessStartInfo
            {
                FileName = "rpm",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-qp");
            startInfo.ArgumentList.Add("--queryformat");
            startInfo.ArgumentList.Add("%{NAME}\\n%{VERSION}\\n%{RELEASE}\\n%{ARCH}\\n%{SIZE}");
            startInfo.ArgumentList.Add(safePath);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogWarning("Failed to start rpm process for {File}", filePath);
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(30));

            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd();
                _logger.LogWarning("rpm -qp failed for {File}: {Error}", filePath, error);
                return null;
            }

            var lines = output.Trim().Split('\n');
            if (lines.Length >= 4)
            {
                return new RpmMetadata(
                    lines[0].Trim(),
                    lines[1].Trim(),
                    lines[2].Trim(),
                    lines[3].Trim(),
                    lines.Length >= 5 && long.TryParse(lines[4].Trim(), out var s) ? s : 0
                );
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract RPM metadata for {File}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Runs createrepo_c on the repository's arch directory.
    /// Uses --update if repodata already exists (preserves existing packages).
    /// Falls back to full createrepo_c if no repodata found.
    /// </summary>
    public async Task<CreaterepoResult> RunCreaterepoAsync(string basePath, string arch)
    {
        EnsureSafeRepoSegments(basePath, arch);
        // SECURITY: Confine the resolved directory to the repository root.
        // basePath/arch are request-derived; confining the combined path stops a
        // crafted basePath from pointing createrepo_c (and rpm metadata reads) at
        // an arbitrary host directory. The confined value is canonicalized.
        var archDir = ProcessArgumentSanitizer.ResolveConfinedPath(
            Path.Combine(basePath.Trim('/'), arch), _reposBasePath);

        if (!Directory.Exists(archDir))
        {
            _logger.LogWarning("Repository directory does not exist: {Dir}", archDir);
            return new CreaterepoResult(false, $"Directory not found: {archDir}");
        }

        var repodataDir = Path.Combine(archDir, "repodata");
        var useUpdate = Directory.Exists(repodataDir) && Directory.EnumerateFileSystemEntries(repodataDir).Any();

        _logger.LogInformation("Running createrepo_c {Args} in {Dir}", useUpdate ? "--update" : "(initial)", archDir);

        try
        {
            // SECURITY: argv is built exclusively via ArgumentList; archDir is the
            // already-confined canonical path, so no shell quoting is involved.
            var startInfo = new ProcessStartInfo
            {
                FileName = "createrepo_c",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = archDir
            };
            if (useUpdate)
            {
                startInfo.ArgumentList.Add("--update");
            }
            startInfo.ArgumentList.Add(archDir);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new CreaterepoResult(false, "Failed to start createrepo_c process");
            }

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                _logger.LogError("createrepo_c failed (exit {Code}): {Stderr}", process.ExitCode, stderr);
                return new CreaterepoResult(false, $"createrepo_c exited with code {process.ExitCode}: {stderr}");
            }

            _logger.LogInformation("createrepo_c completed successfully for {Dir}: {Output}", archDir, stdout);
            return new CreaterepoResult(true, stdout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run createrepo_c for {Dir}", archDir);
            return new CreaterepoResult(false, $"Exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs createrepo_c --update for all arch subdirectories in a repository.
    /// </summary>
    public async Task<List<CreaterepoResult>> SyncAllArchAsync(string basePath)
    {
        EnsureSafeRepoSegments(basePath, arch: null);
        var repoDir = ProcessArgumentSanitizer.ResolveConfinedPath(basePath.Trim('/'), _reposBasePath);
        var results = new List<CreaterepoResult>();

        if (!Directory.Exists(repoDir))
        {
            _logger.LogWarning("Repository base directory does not exist: {Dir}", repoDir);
            results.Add(new CreaterepoResult(false, $"Directory not found: {repoDir}"));
            return results;
        }

        // Find all arch subdirectories (e.g., x86_64/, aarch64/, noarch/)
        foreach (var archDir in Directory.GetDirectories(repoDir))
        {
            var archName = Path.GetFileName(archDir);
            // Skip repodata directory itself
            if (archName == "repodata") continue;

            // Only process directories that contain .rpm files
            var hasRpms = Directory.EnumerateFiles(archDir, "*.rpm").Any();
            if (!hasRpms) continue;

            _logger.LogInformation("Syncing arch directory: {Dir}", archDir);
            var result = await RunCreaterepoAsync(basePath, archName);
            results.Add(result);
        }

        // If no arch subdirectories found, try the base directory itself
        if (results.Count == 0)
        {
            var hasRpms = Directory.EnumerateFiles(repoDir, "*.rpm").Any();
            if (hasRpms)
            {
                // RPMs directly in basePath without arch subdirectory
                var result = await RunCreaterepoAsync(basePath, ".");
                results.Add(result);
            }
            else
            {
                results.Add(new CreaterepoResult(false, "No RPM files found in repository"));
            }
        }

        return results;
    }

    /// <summary>
    /// Checks if repodata/repomd.xml exists for a given arch directory.
    /// </summary>
    public bool HasRepodata(string basePath, string arch)
    {
        EnsureSafeRepoSegments(basePath, arch);
        var archDir = ProcessArgumentSanitizer.ResolveConfinedPath(
            Path.Combine(basePath.Trim('/'), arch), _reposBasePath);
        var repomdPath = Path.Combine(archDir, "repodata", "repomd.xml");
        return File.Exists(repomdPath);
    }

    /// <summary>
    /// Lists all RPM files in a repository's arch directory.
    /// </summary>
    public List<string> ListRpmFiles(string basePath, string arch)
    {
        EnsureSafeRepoSegments(basePath, arch);
        var archDir = ProcessArgumentSanitizer.ResolveConfinedPath(
            Path.Combine(basePath.Trim('/'), arch), _reposBasePath);
        if (!Directory.Exists(archDir))
            return [];

        return Directory.EnumerateFiles(archDir, "*.rpm")
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .ToList();
    }
}

public record RpmMetadata(string Name, string Version, string Release, string Arch, long Size);

public record CreaterepoResult(bool Success, string Output);