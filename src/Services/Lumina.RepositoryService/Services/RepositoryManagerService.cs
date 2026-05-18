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
    /// Ensures the repository directory structure exists: {basePath}/{arch}/
    /// </summary>
    public string EnsureRepoDir(string basePath, string arch)
    {
        var archDir = Path.Combine(_reposBasePath, basePath.Trim('/'), arch);
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
            // SECURITY: Validate file path to prevent command injection
            ProcessArgumentSanitizer.SanitizeFilePath(filePath);

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
            startInfo.ArgumentList.Add(filePath);

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
        var archDir = Path.Combine(_reposBasePath, basePath.Trim('/'), arch);

        if (!Directory.Exists(archDir))
        {
            _logger.LogWarning("Repository directory does not exist: {Dir}", archDir);
            return new CreaterepoResult(false, $"Directory not found: {archDir}");
        }

        var repodataDir = Path.Combine(archDir, "repodata");
        var useUpdate = Directory.Exists(repodataDir) && Directory.EnumerateFileSystemEntries(repodataDir).Any();

        var arguments = useUpdate
            ? $"--update \"{archDir}\""
            : $"\"{archDir}\"";

        _logger.LogInformation("Running createrepo_c {Args} in {Dir}", useUpdate ? "--update" : "(initial)", archDir);

        try
        {
            // SECURITY: Validate paths to prevent command injection
            ProcessArgumentSanitizer.SanitizeFilePath(archDir);

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
        var repoDir = Path.Combine(_reposBasePath, basePath.Trim('/'));
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
        var repomdPath = Path.Combine(_reposBasePath, basePath.Trim('/'), arch, "repodata", "repomd.xml");
        return File.Exists(repomdPath);
    }

    /// <summary>
    /// Lists all RPM files in a repository's arch directory.
    /// </summary>
    public List<string> ListRpmFiles(string basePath, string arch)
    {
        var archDir = Path.Combine(_reposBasePath, basePath.Trim('/'), arch);
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