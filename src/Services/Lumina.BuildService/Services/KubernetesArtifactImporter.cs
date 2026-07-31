using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumina.BuildService.Data;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

internal interface IKubernetesArtifactImporter
{
    Task<int> ImportAsync(Guid buildJobId, CancellationToken cancellationToken);
}

/// <summary>
/// Imports untrusted runner output into the trusted build boundary. The server
/// derives every object name, recomputes content metadata, reads each RPM header,
/// and persists the complete set atomically before pipeline advancement.
/// </summary>
internal sealed class KubernetesArtifactImporter : IKubernetesArtifactImporter
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    private readonly BuildDbContext _db;
    private readonly IKubernetesArtifactObjectStore _objects;
    private readonly IRpmArtifactValidator _rpmValidator;
    private readonly ILogger<KubernetesArtifactImporter> _logger;
    private readonly string _artifactRoot;

    public KubernetesArtifactImporter(
        BuildDbContext db,
        IKubernetesArtifactObjectStore objects,
        IRpmArtifactValidator rpmValidator,
        IConfiguration configuration,
        ILogger<KubernetesArtifactImporter> logger)
    {
        _db = db;
        _objects = objects;
        _rpmValidator = rpmValidator;
        _logger = logger;
        _artifactRoot = Path.GetFullPath(
            configuration["Kubernetes:Artifacts:LocalRoot"] ?? "/app/builds");
        if (string.Equals(_artifactRoot, Path.GetPathRoot(_artifactRoot), StringComparison.Ordinal))
            throw new InvalidOperationException("Kubernetes artifact root cannot be a filesystem root.");
    }

    public async Task<int> ImportAsync(
        Guid buildJobId,
        CancellationToken cancellationToken)
    {
        var job = await _db.BuildJobs
            .Include(item => item.Artifacts)
            .SingleAsync(item => item.Id == buildJobId, cancellationToken);
        EnsureImportable(job);

        var manifestObject = KubernetesArtifactManifestPolicy.ManifestObjectName(
            job.Id,
            job.KubernetesJobUid!);
        var manifestBytes = await _objects.ReadManifestAsync(
            manifestObject,
            MaximumManifestBytes,
            cancellationToken);
        var manifest = DeserializeManifest(manifestBytes);
        var validated = KubernetesArtifactManifestPolicy.Validate(manifest, job);

        if (job.KubernetesArtifactsImportedAt != null)
        {
            ValidateRecordedImport(job, validated);
            return job.Artifacts.Count;
        }
        if (job.Artifacts.Count != 0 || job.KubernetesArtifactManifestSha256 != null)
            throw new ValidationException("Kubernetes artifact import state is incomplete.");

        var jobDirectory = Path.Combine(_artifactRoot, job.Id.ToString());
        var temporaryDirectory = Path.Combine(
            jobDirectory,
            $".import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var imported = new List<BuildArtifact>(validated.Artifacts.Count);
            foreach (var entry in validated.Artifacts)
            {
                var finalPath = Path.Combine(jobDirectory, entry.FileName);
                var verified = await MaterializeAndVerifyAsync(
                    job,
                    entry,
                    temporaryDirectory,
                    finalPath,
                    cancellationToken);
                imported.Add(new BuildArtifact
                {
                    Id = Guid.NewGuid(),
                    BuildJobId = job.Id,
                    FileName = entry.FileName,
                    FilePath = finalPath,
                    FileSize = verified.Size,
                    HashSha256 = verified.Sha256,
                    HashMd5 = verified.Md5,
                    RpmNevra = verified.Nevra,
                    SourceStoragePath = entry.ObjectName,
                    StoragePath = string.Empty,
                    CveScanStatus = ScanStatus.Pending,
                    CreatedAt = DateTime.UtcNow
                });
            }

            _db.BuildArtifacts.AddRange(imported);
            job.KubernetesArtifactManifestSha256 = validated.ManifestSha256;
            job.KubernetesArtifactsImportedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Imported and verified {ArtifactCount} Kubernetes RPM artifacts for build {BuildJobId} from manifest {ManifestSha256}",
                imported.Count,
                job.Id,
                validated.ManifestSha256);
            return imported.Count;
        }
        finally
        {
            TryDeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    private async Task<VerifiedArtifact> MaterializeAndVerifyAsync(
        BuildJob job,
        KubernetesArtifactManifestEntry entry,
        string temporaryDirectory,
        string finalPath,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(temporaryDirectory, entry.FileName);
        await _objects.DownloadArtifactAsync(
            entry.ObjectName,
            temporaryPath,
            entry.Size,
            cancellationToken);
        var verified = await VerifyFileAsync(job, entry, temporaryPath, cancellationToken);
        if (File.Exists(finalPath))
        {
            await VerifyFileAsync(job, entry, finalPath, cancellationToken);
            return verified;
        }
        try
        {
            File.Move(temporaryPath, finalPath);
            return verified;
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            // A previous monitor may have completed the same immutable download
            // before losing its lease. Accept it only after full revalidation.
            return await VerifyFileAsync(job, entry, finalPath, cancellationToken);
        }
    }

    private async Task<VerifiedArtifact> VerifyFileAsync(
        BuildJob job,
        KubernetesArtifactManifestEntry entry,
        string path,
        CancellationToken cancellationToken)
    {
        var hashes = await HashFileAsync(path, entry.Size, cancellationToken);
        if (!string.Equals(hashes.Sha256, entry.Sha256, StringComparison.Ordinal))
            throw new ValidationException($"Kubernetes artifact '{entry.FileName}' failed SHA-256 verification.");

        var rpm = await _rpmValidator.ValidateAsync(path, cancellationToken);
        if (!rpm.IsValid ||
            string.IsNullOrWhiteSpace(rpm.Nevra) ||
            string.IsNullOrWhiteSpace(rpm.Architecture) ||
            string.IsNullOrWhiteSpace(rpm.ExpectedFileName))
        {
            throw new ValidationException(
                $"Kubernetes artifact '{entry.FileName}' is not a valid RPM: {rpm.Error ?? "missing RPM identity"}.");
        }
        if (!string.Equals(rpm.Architecture, job.TargetArchitecture, StringComparison.Ordinal) &&
            !string.Equals(rpm.Architecture, "noarch", StringComparison.Ordinal))
        {
            throw new ValidationException(
                $"Kubernetes artifact '{entry.FileName}' architecture '{rpm.Architecture}' does not match target '{job.TargetArchitecture}'.");
        }
        if (!string.Equals(rpm.ExpectedFileName, entry.FileName, StringComparison.Ordinal))
        {
            throw new ValidationException(
                $"Kubernetes artifact filename '{entry.FileName}' does not match RPM identity '{rpm.ExpectedFileName}'.");
        }
        if (rpm.Nevra.Length > 512)
            throw new ValidationException($"Kubernetes artifact '{entry.FileName}' NEVRA is too long.");

        return new VerifiedArtifact(entry.Size, hashes.Sha256, hashes.Md5, rpm.Nevra);
    }

    private static async Task<ArtifactHashes> HashFileAsync(
        string path,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                    break;
                total = checked(total + read);
                if (total > expectedSize)
                    throw new ValidationException("Kubernetes artifact file exceeds its declared size.");
                sha256.AppendData(buffer, 0, read);
                md5.AppendData(buffer, 0, read);
            }
            if (total != expectedSize)
                throw new ValidationException("Kubernetes artifact file size does not match its manifest.");
            return new ArtifactHashes(
                Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant(),
                Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant());
        }
        catch (OverflowException exception)
        {
            throw new ValidationException("Kubernetes artifact file size overflowed.", exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static KubernetesArtifactManifest DeserializeManifest(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<KubernetesArtifactManifest>(bytes, ManifestJson)
                   ?? throw new ValidationException("Kubernetes artifact manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new ValidationException("Kubernetes artifact manifest JSON is invalid.", exception);
        }
    }

    private static void EnsureImportable(BuildJob job)
    {
        if (job.ExecutionBackend != BuildExecutorBackend.Kubernetes ||
            job.Status != BuildStatus.Building ||
            string.IsNullOrWhiteSpace(job.KubernetesJobUid))
        {
            throw new ValidationException("Build is not eligible for Kubernetes artifact import.");
        }
    }

    private static void ValidateRecordedImport(
        BuildJob job,
        ValidatedKubernetesArtifactManifest manifest)
    {
        if (!string.Equals(
                job.KubernetesArtifactManifestSha256,
                manifest.ManifestSha256,
                StringComparison.Ordinal) ||
            job.Artifacts.Count != manifest.Artifacts.Count)
        {
            throw new ValidationException("Kubernetes artifact manifest changed after import.");
        }

        var recorded = job.Artifacts.ToDictionary(item => item.FileName, StringComparer.Ordinal);
        foreach (var entry in manifest.Artifacts)
        {
            if (!recorded.TryGetValue(entry.FileName, out var artifact) ||
                !string.Equals(artifact.SourceStoragePath, entry.ObjectName, StringComparison.Ordinal) ||
                !string.Equals(artifact.HashSha256, entry.Sha256, StringComparison.Ordinal) ||
                artifact.FileSize != entry.Size ||
                string.IsNullOrWhiteSpace(artifact.RpmNevra))
            {
                throw new ValidationException("Recorded Kubernetes artifact provenance is inconsistent.");
            }
        }
    }

    private void TryDeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not remove Kubernetes artifact import directory {Directory}", path);
        }
    }

    private sealed record ArtifactHashes(string Sha256, string Md5);
    private sealed record VerifiedArtifact(long Size, string Sha256, string Md5, string Nevra);
}
