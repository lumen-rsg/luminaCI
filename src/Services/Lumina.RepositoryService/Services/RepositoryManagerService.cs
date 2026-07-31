using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumina.Shared.Errors;
using Lumina.Shared.Extensions;

namespace Lumina.RepositoryService.Services;

/// <summary>
/// Manages RPM repository filesystem structure and createrepo_c metadata.
/// </summary>
public class RepositoryManagerService
{
    private const int AtFdcwd = -100;
    private const uint RenameExchange = 2;
    private static readonly Regex RpmFieldPattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9+._~^-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

    /// <summary>Creates a private operation directory on the repository volume.</summary>
    public string CreatePublicationStagingDirectory(Guid repositoryId)
    {
        var stagingDirectory = ProcessArgumentSanitizer.ResolveConfinedPath(
            Path.Combine(".staging", repositoryId.ToString("N"), Guid.NewGuid().ToString("N")),
            _reposBasePath);
        Directory.CreateDirectory(stagingDirectory);
        return stagingDirectory;
    }

    public async Task<string> WriteStagedRpmAsync(string stagingDirectory, string fileName, Stream content)
    {
        var safeName = Path.GetFileName(fileName);
        if (safeName != fileName || !safeName.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("The package filename must be a plain .rpm filename.");

        var safeDirectory = ProcessArgumentSanitizer.ResolveConfinedPath(stagingDirectory, _reposBasePath);
        var stagedPath = ProcessArgumentSanitizer.ResolveConfinedPath(
            Path.Combine(safeDirectory, safeName), _reposBasePath);
        await using var output = new FileStream(
            stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await content.CopyToAsync(output);
        await output.FlushAsync();
        return stagedPath;
    }

    public string GetStagedRpmPath(string stagingDirectory, string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        if (safeName != fileName || !safeName.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("The package filename must be a plain .rpm filename.");

        var safeDirectory = ProcessArgumentSanitizer.ResolveConfinedPath(stagingDirectory, _reposBasePath);
        var stagedPath = ProcessArgumentSanitizer.ResolveConfinedPath(
            Path.Combine(safeDirectory, safeName), _reposBasePath);
        if (File.Exists(stagedPath))
            throw new ConflictException("The staged package already exists.");
        return stagedPath;
    }

    public string GetLiveRpmPath(string basePath, string arch, string fileName)
    {
        EnsureSafeRepoSegments(basePath, arch);
        var safeName = Path.GetFileName(fileName);
        if (safeName != fileName || !safeName.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("Live package filename is invalid.");
        var path = ProcessArgumentSanitizer.ResolveConfinedPath(
            Path.Combine(basePath.Trim('/'), arch, safeName), _reposBasePath);
        if (!File.Exists(path))
            throw new ValidationException($"Live package '{safeName}' is missing from repository storage.");
        return path;
    }

    public async Task GenerateGateMetadataAsync(string directory, long revision)
    {
        var safeDirectory = ProcessArgumentSanitizer.ResolveConfinedPath(directory, _reposBasePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = "createrepo_c",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = safeDirectory
        };
        startInfo.ArgumentList.Add("--revision");
        startInfo.ArgumentList.Add(revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--set-timestamp-to-revision");
        startInfo.ArgumentList.Add("--simple-md-filenames");
        startInfo.ArgumentList.Add(safeDirectory);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start createrepo_c for promotion gate.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Promotion gate createrepo_c failed: {await stderr}");
        _logger.LogInformation(
            "Generated deterministic promotion gate metadata in {Directory}: {Output}",
            safeDirectory, await stdout);
    }

    public RpmMetadata ValidateStagedRpm(string stagedPath, string expectedFileName)
    {
        var metadata = ExtractRpmMetadata(stagedPath)
            ?? throw new ValidationException("The staged file is not a readable RPM.");
        if (!RpmFieldPattern.IsMatch(metadata.Name) ||
            !RpmFieldPattern.IsMatch(metadata.Version) ||
            !RpmFieldPattern.IsMatch(metadata.Release))
        {
            throw new ValidationException("The RPM contains invalid NEVR header fields.");
        }
        ProcessArgumentSanitizer.ValidateRepositoryArch(metadata.Arch);

        var canonicalName = $"{metadata.Name}-{metadata.Version}-{metadata.Release}.{metadata.Arch}.rpm";
        if (!string.Equals(expectedFileName, canonicalName, StringComparison.Ordinal))
        {
            throw new ValidationException(
                $"RPM filename does not match its NEVRA header; expected '{canonicalName}'.");
        }
        return metadata;
    }

    /// <summary>
    /// Builds complete metadata from a private snapshot containing the current
    /// live RPM set plus the candidate package.
    /// </summary>
    public async Task<string> GenerateStagedMetadataAsync(
        string basePath,
        string arch,
        string stagingDirectory,
        string stagedRpmPath)
    {
        EnsureSafeRepoSegments(basePath, arch);
        var liveDirectory = EnsureRepoDir(basePath, arch);
        var snapshotDirectory = ProcessArgumentSanitizer.ResolveConfinedPath(
            Path.Combine(stagingDirectory, "snapshot"), _reposBasePath);
        Directory.CreateDirectory(snapshotDirectory);

        foreach (var liveRpm in Directory.EnumerateFiles(liveDirectory, "*.rpm"))
            File.Copy(liveRpm, Path.Combine(snapshotDirectory, Path.GetFileName(liveRpm)));

        var candidateName = Path.GetFileName(stagedRpmPath);
        var snapshotCandidate = Path.Combine(snapshotDirectory, candidateName);
        if (File.Exists(snapshotCandidate))
            throw new ConflictException($"Package file '{candidateName}' already exists in this repository.");
        File.Copy(stagedRpmPath, snapshotCandidate);

        var result = await RunCreaterepoForDirectoryAsync(snapshotDirectory);
        if (!result.Success)
            throw new InvalidOperationException($"createrepo_c failed in publication staging: {result.Output}");

        var repodata = Path.Combine(snapshotDirectory, "repodata");
        if (!File.Exists(Path.Combine(repodata, "repomd.xml")))
            throw new InvalidOperationException("createrepo_c completed without producing repomd.xml.");
        return snapshotDirectory;
    }

    /// <summary>
    /// Atomically exchanges the complete architecture directory, so clients see
    /// either the old package+metadata set or the new set—never a partial mix.
    /// The old snapshot is retained until the database transaction commits.
    /// </summary>
    public PublicationCommit CommitStagedPublication(
        string basePath,
        string arch,
        string stagedSnapshotPath,
        string candidateFileName)
    {
        stagedSnapshotPath = ProcessArgumentSanitizer.ResolveConfinedPath(stagedSnapshotPath, _reposBasePath);
        var liveDirectory = EnsureRepoDir(basePath, arch);
        var snapshotCandidate = Path.Combine(stagedSnapshotPath, Path.GetFileName(candidateFileName));
        if (!File.Exists(snapshotCandidate) ||
            !File.Exists(Path.Combine(stagedSnapshotPath, "repodata", "repomd.xml")))
        {
            throw new InvalidOperationException("Publication snapshot is incomplete.");
        }

        AtomicExchange(liveDirectory, stagedSnapshotPath);
        return new PublicationCommit(
            Path.Combine(liveDirectory, Path.GetFileName(candidateFileName)),
            liveDirectory,
            stagedSnapshotPath);
    }

    public void CompletePublication(PublicationCommit publication)
    {
        if (Directory.Exists(publication.PreviousSnapshotPath))
        {
            Directory.Delete(publication.PreviousSnapshotPath, recursive: true);
        }
    }

    public void RollbackPublication(PublicationCommit publication)
    {
        if (Directory.Exists(publication.LiveDirectoryPath) &&
            Directory.Exists(publication.PreviousSnapshotPath))
        {
            AtomicExchange(publication.LiveDirectoryPath, publication.PreviousSnapshotPath);
            Directory.Delete(publication.PreviousSnapshotPath, recursive: true);
        }
    }

    public void CleanupPublicationStaging(string stagingDirectory)
    {
        var safeDirectory = ProcessArgumentSanitizer.ResolveConfinedPath(stagingDirectory, _reposBasePath);
        if (Directory.Exists(safeDirectory))
            Directory.Delete(safeDirectory, recursive: true);
    }

    public void WritePublicationJournal(
        string stagingDirectory,
        Guid packageId,
        string basePath,
        string arch,
        string fileName)
    {
        EnsureSafeRepoSegments(basePath, arch);
        if (Path.GetFileName(fileName) != fileName)
            throw new ValidationException("Publication journal contains an invalid filename.");
        var journalPath = ProcessArgumentSanitizer.ResolveConfinedPath(
            Path.Combine(stagingDirectory, "publication.json"), _reposBasePath);
        var journal = new PublicationJournal(packageId, basePath, arch, fileName, stagingDirectory);
        using var stream = new FileStream(
            journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, journal);
        stream.Flush(flushToDisk: true);
    }

    public bool HasPublicationJournal(string stagingDirectory)
    {
        var journalPath = ProcessArgumentSanitizer.ResolveConfinedPath(
            Path.Combine(stagingDirectory, "publication.json"), _reposBasePath);
        return File.Exists(journalPath);
    }

    public void RemovePublicationJournal(string stagingDirectory)
    {
        var journalPath = ProcessArgumentSanitizer.ResolveConfinedPath(
            Path.Combine(stagingDirectory, "publication.json"), _reposBasePath);
        if (File.Exists(journalPath))
            File.Delete(journalPath);
    }

    public IReadOnlyList<(string JournalPath, PublicationJournal Journal)> ReadPublicationJournals()
    {
        var stagingRoot = Path.Combine(_reposBasePath, ".staging");
        if (!Directory.Exists(stagingRoot))
            return [];

        var journals = new List<(string, PublicationJournal)>();
        foreach (var operationDirectory in Directory.EnumerateDirectories(stagingRoot, "*", SearchOption.AllDirectories)
                     .Where(path => File.Exists(Path.Combine(path, "publication.json"))))
        {
            var journalPath = Path.Combine(operationDirectory, "publication.json");
            var journal = JsonSerializer.Deserialize<PublicationJournal>(File.ReadAllText(journalPath))
                ?? throw new InvalidOperationException($"Invalid publication journal: {journalPath}");
            journals.Add((journalPath, journal));
        }
        return journals;
    }

    public void RecoverPublication(PublicationJournal journal, bool databaseCommitted)
    {
        EnsureSafeRepoSegments(journal.BasePath, journal.Arch);
        var stagingDirectory = ProcessArgumentSanitizer.ResolveConfinedPath(
            journal.StagingDirectory, _reposBasePath);
        var previousOrCandidateSnapshot = ProcessArgumentSanitizer.ResolveConfinedPath(
            Path.Combine(stagingDirectory, "snapshot"), _reposBasePath);
        var liveDirectory = ProcessArgumentSanitizer.ResolveConfinedPath(
            Path.Combine(journal.BasePath.Trim('/'), journal.Arch), _reposBasePath);
        var liveCandidate = Path.Combine(liveDirectory, Path.GetFileName(journal.FileName));

        if (!databaseCommitted &&
            File.Exists(liveCandidate) &&
            Directory.Exists(liveDirectory) &&
            Directory.Exists(previousOrCandidateSnapshot))
        {
            AtomicExchange(liveDirectory, previousOrCandidateSnapshot);
        }

        CleanupPublicationStaging(stagingDirectory);
    }

    public void CleanupOrphanedPublicationStaging()
    {
        var stagingRoot = Path.Combine(_reposBasePath, ".staging");
        if (!Directory.Exists(stagingRoot))
            return;

        foreach (var repositoryDirectory in Directory.EnumerateDirectories(stagingRoot))
        {
            foreach (var operationDirectory in Directory.EnumerateDirectories(repositoryDirectory))
            {
                if (!File.Exists(Path.Combine(operationDirectory, "publication.json")))
                    CleanupPublicationStaging(operationDirectory);
            }
        }
    }

    private async Task<CreaterepoResult> RunCreaterepoForDirectoryAsync(string directory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "createrepo_c",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory
        };
        startInfo.ArgumentList.Add(directory);

        using var process = Process.Start(startInfo);
        if (process is null)
            return new CreaterepoResult(false, "Failed to start createrepo_c.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;
        return process.ExitCode == 0
            ? new CreaterepoResult(true, output)
            : new CreaterepoResult(false, $"createrepo_c exited with code {process.ExitCode}: {error}");
    }

    private static void AtomicExchange(string firstPath, string secondPath)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Atomic repository metadata exchange requires Linux renameat2.");
        if (renameat2(AtFdcwd, firstPath, AtFdcwd, secondPath, RenameExchange) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Atomic repository metadata exchange failed.");
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "renameat2")]
    private static extern int renameat2(
        int oldDirectoryFileDescriptor,
        string oldPath,
        int newDirectoryFileDescriptor,
        string newPath,
        uint flags);
}

public record RpmMetadata(string Name, string Version, string Release, string Arch, long Size);

public record CreaterepoResult(bool Success, string Output);

public record PublicationCommit(
    string FinalRpmPath,
    string LiveDirectoryPath,
    string PreviousSnapshotPath);

public record PublicationJournal(
    Guid PackageId,
    string BasePath,
    string Arch,
    string FileName,
    string StagingDirectory);
