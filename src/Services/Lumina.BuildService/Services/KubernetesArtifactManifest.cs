using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;

namespace Lumina.BuildService.Services;

public sealed record KubernetesArtifactManifest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("buildJobId")] Guid BuildJobId,
    [property: JsonPropertyName("kubernetesJobUid")] string KubernetesJobUid,
    [property: JsonPropertyName("runnerImageDigest")] string RunnerImageDigest,
    [property: JsonPropertyName("targetDistribution")] string TargetDistribution,
    [property: JsonPropertyName("targetRelease")] string TargetRelease,
    [property: JsonPropertyName("targetArchitecture")] string TargetArchitecture,
    [property: JsonPropertyName("artifacts")] IReadOnlyList<KubernetesArtifactManifestEntry> Artifacts);

public sealed record KubernetesArtifactManifestEntry(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("objectName")] string ObjectName,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record ValidatedKubernetesArtifactManifest(
    string ManifestSha256,
    IReadOnlyList<KubernetesArtifactManifestEntry> Artifacts);

/// <summary>
/// Validates the untrusted runner-authored artifact manifest against immutable
/// server-side build provenance. Object names are derived, never accepted as
/// arbitrary storage paths.
/// </summary>
public static class KubernetesArtifactManifestPolicy
{
    public const int CurrentVersion = 1;
    public const int MaximumArtifacts = 256;
    public const long MaximumArtifactBytes = 8L * 1024 * 1024 * 1024;
    public const long MaximumTotalBytes = 32L * 1024 * 1024 * 1024;

    public static ValidatedKubernetesArtifactManifest Validate(
        KubernetesArtifactManifest manifest,
        BuildJob job)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(job);
        if (manifest.Version != CurrentVersion)
            throw new ValidationException("Kubernetes artifact manifest version is unsupported.");
        if (job.ExecutionBackend != BuildExecutorBackend.Kubernetes ||
            job.Id == Guid.Empty ||
            manifest.BuildJobId != job.Id ||
            string.IsNullOrWhiteSpace(job.KubernetesJobUid) ||
            !string.Equals(manifest.KubernetesJobUid, job.KubernetesJobUid, StringComparison.Ordinal) ||
            !IsSafeUid(manifest.KubernetesJobUid))
        {
            throw new ValidationException("Kubernetes artifact manifest Job identity is invalid.");
        }
        if (string.IsNullOrWhiteSpace(job.RunnerImageDigest) ||
            !string.Equals(manifest.RunnerImageDigest, job.RunnerImageDigest, StringComparison.Ordinal) ||
            !string.Equals(manifest.TargetDistribution, job.TargetDistribution, StringComparison.Ordinal) ||
            !string.Equals(manifest.TargetRelease, job.TargetRelease, StringComparison.Ordinal) ||
            !string.Equals(manifest.TargetArchitecture, job.TargetArchitecture, StringComparison.Ordinal))
        {
            throw new ValidationException("Kubernetes artifact manifest build provenance does not match the job.");
        }
        if (manifest.Artifacts is not { Count: > 0 and <= MaximumArtifacts })
            throw new ValidationException("Kubernetes artifact manifest must contain a bounded non-empty artifact set.");

        var normalized = new List<KubernetesArtifactManifestEntry>(manifest.Artifacts.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var objects = new HashSet<string>(StringComparer.Ordinal);
        var digests = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var entry in manifest.Artifacts)
        {
            if (entry is null)
                throw new ValidationException("Kubernetes artifact manifest contains an empty entry.");
            if (!IsSafeRpmFileName(entry.FileName))
                throw new ValidationException("Kubernetes artifact filename is invalid.");
            if (entry.Size is <= 0 or > MaximumArtifactBytes)
                throw new ValidationException("Kubernetes artifact size is outside the allowed range.");
            string digest;
            try
            {
                digest = NormalizeSha256(entry.Sha256);
                totalBytes = checked(totalBytes + entry.Size);
            }
            catch (OverflowException exception)
            {
                throw new ValidationException("Kubernetes artifact total size overflowed.", exception);
            }
            if (totalBytes > MaximumTotalBytes)
                throw new ValidationException("Kubernetes artifact set exceeds the total size limit.");

            var expectedObject = ObjectName(job.Id, manifest.KubernetesJobUid, digest, entry.FileName);
            if (!string.Equals(entry.ObjectName, expectedObject, StringComparison.Ordinal))
                throw new ValidationException("Kubernetes artifact object path is not content-addressed for this job.");
            if (!names.Add(entry.FileName) || !objects.Add(entry.ObjectName) || !digests.Add(digest))
                throw new ValidationException("Kubernetes artifact manifest contains duplicate identity.");
            normalized.Add(entry with { Sha256 = digest });
        }

        normalized.Sort((left, right) => string.CompareOrdinal(left.FileName, right.FileName));
        return new ValidatedKubernetesArtifactManifest(
            CanonicalHash(manifest, normalized),
            normalized);
    }

    public static string ManifestObjectName(Guid buildJobId, string jobUid)
    {
        if (buildJobId == Guid.Empty || !IsSafeUid(jobUid))
            throw new ValidationException("Kubernetes artifact manifest object identity is invalid.");
        return $"jobs/{buildJobId:N}/{jobUid}/manifest.json";
    }

    public static string ObjectName(Guid buildJobId, string jobUid, string sha256, string fileName)
    {
        if (buildJobId == Guid.Empty || !IsSafeUid(jobUid) || !IsSafeRpmFileName(fileName))
            throw new ValidationException("Kubernetes artifact object identity is invalid.");
        var digest = NormalizeSha256(sha256);
        return $"jobs/{buildJobId:N}/{jobUid}/sha256/{digest}/{fileName}";
    }

    private static string CanonicalHash(
        KubernetesArtifactManifest manifest,
        IReadOnlyList<KubernetesArtifactManifestEntry> artifacts)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", CurrentVersion);
            writer.WriteString("buildJobId", manifest.BuildJobId);
            writer.WriteString("kubernetesJobUid", manifest.KubernetesJobUid);
            writer.WriteString("runnerImageDigest", manifest.RunnerImageDigest);
            writer.WriteString("targetDistribution", manifest.TargetDistribution);
            writer.WriteString("targetRelease", manifest.TargetRelease);
            writer.WriteString("targetArchitecture", manifest.TargetArchitecture);
            writer.WriteStartArray("artifacts");
            foreach (var artifact in artifacts)
            {
                writer.WriteStartObject();
                writer.WriteString("fileName", artifact.FileName);
                writer.WriteString("objectName", artifact.ObjectName);
                writer.WriteNumber("size", artifact.Size);
                writer.WriteString("sha256", artifact.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static string NormalizeSha256(string value)
    {
        var digest = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (digest.Length != 64 || digest.Any(character => !Uri.IsHexDigit(character)))
            throw new ValidationException("Kubernetes artifact SHA-256 digest is invalid.");
        return digest;
    }

    private static bool IsSafeUid(string value) =>
        value is { Length: >= 1 and <= 128 } &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool IsSafeRpmFileName(string value) =>
        value is { Length: >= 5 and <= 512 } &&
        string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) &&
        value.EndsWith(".rpm", StringComparison.Ordinal) &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '+' or '-');
}
