using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumina.Shared.Errors;
using Lumina.Shared.Events;
using Lumina.Shared.Extensions;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;

namespace Lumina.BuildService.Services;

public sealed record NativePromotionGateCandidate(
    [property: JsonPropertyName("artifactId")] Guid ArtifactId,
    [property: JsonPropertyName("candidatePackageId")] Guid CandidatePackageId,
    [property: JsonPropertyName("projectPackageId")] string ProjectPackageId,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("objectName")] string ObjectName,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record NativePromotionGateManifest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("promotionSetId")] Guid PromotionSetId,
    [property: JsonPropertyName("repositoryId")] Guid RepositoryId,
    [property: JsonPropertyName("promotionGroup")] string PromotionGroup,
    [property: JsonPropertyName("targetArchitecture")] string TargetArchitecture,
    [property: JsonPropertyName("runnerImageDigest")] string RunnerImageDigest,
    [property: JsonPropertyName("candidates")] IReadOnlyList<NativePromotionGateCandidate> Candidates);

public static class NativePromotionGateManifestPolicy
{
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static NativePromotionGate Create(
        Guid deliveryId,
        Guid promotionSetId,
        Guid repositoryId,
        string promotionGroup,
        string targetArchitecture,
        string runnerImageDigest,
        IEnumerable<(BuildJob Job, BuildArtifact Artifact)> artifacts,
        DateTime now)
    {
        if (deliveryId == Guid.Empty || promotionSetId == Guid.Empty || repositoryId == Guid.Empty)
            throw new ValidationException("Native promotion gate identity is invalid.");
        var group = PromotionSetIdentity.NormalizeGroup(promotionGroup);
        ProcessArgumentSanitizer.ValidateRepositoryArch(targetArchitecture);
        var digest = NormalizeDigest(runnerImageDigest);
        var candidates = artifacts.Select(item => CreateCandidate(
                item.Job, item.Artifact, deliveryId, promotionSetId, repositoryId))
            .OrderBy(item => item.ProjectPackageId, StringComparer.Ordinal)
            .ThenBy(item => item.FileName, StringComparer.Ordinal)
            .ThenBy(item => item.ArtifactId)
            .ToList();
        if (candidates.Count is < 1 or > 4096 ||
            candidates.Select(item => item.ArtifactId).Distinct().Count() != candidates.Count ||
            candidates.Select(item => item.CandidatePackageId).Distinct().Count() != candidates.Count)
        {
            throw new ValidationException("Native promotion gate candidates are empty or ambiguous.");
        }

        var manifest = new NativePromotionGateManifest(
            CurrentVersion, promotionSetId, repositoryId, group,
            targetArchitecture, digest, candidates);
        var json = JsonSerializer.Serialize(manifest, Json);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return new NativePromotionGate
        {
            Id = promotionSetId,
            ProjectWebhookDeliveryId = deliveryId,
            RepositoryId = repositoryId,
            PromotionGroup = group,
            TargetArchitecture = targetArchitecture,
            RunnerImageDigest = digest,
            CandidateManifestJson = json,
            CandidateManifestSha256 = hash,
            Status = NativePromotionGateStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static NativePromotionGateManifest Read(NativePromotionGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        NativePromotionGateManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<NativePromotionGateManifest>(
                gate.CandidateManifestJson, Json)
                ?? throw new JsonException("Manifest is missing.");
        }
        catch (JsonException exception)
        {
            throw new ValidationException("Native promotion gate manifest is invalid.", exception);
        }
        var expected = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(gate.CandidateManifestJson))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(gate.CandidateManifestSha256 ?? string.Empty)) ||
            manifest.Version != CurrentVersion || manifest.PromotionSetId != gate.Id ||
            manifest.RepositoryId != gate.RepositoryId ||
            !string.Equals(manifest.PromotionGroup, gate.PromotionGroup, StringComparison.Ordinal) ||
            !string.Equals(manifest.TargetArchitecture, gate.TargetArchitecture, StringComparison.Ordinal) ||
            !string.Equals(manifest.RunnerImageDigest, gate.RunnerImageDigest, StringComparison.Ordinal) ||
            manifest.Candidates is not { Count: > 0 and <= 4096 })
        {
            throw new ValidationException("Native promotion gate manifest identity changed.");
        }
        if (manifest.Candidates.Select(item => item.ArtifactId).Distinct().Count() != manifest.Candidates.Count ||
            manifest.Candidates.Select(item => item.CandidatePackageId).Distinct().Count() != manifest.Candidates.Count ||
            manifest.Candidates.Sum(item => item.Size) > 32L * 1024 * 1024 * 1024)
            throw new ValidationException("Native promotion gate candidate set is ambiguous or oversized.");
        foreach (var candidate in manifest.Candidates)
        {
            var fileName = Path.GetFileName(candidate.FileName);
            if (candidate.ArtifactId == Guid.Empty || candidate.CandidatePackageId == Guid.Empty ||
                string.IsNullOrWhiteSpace(candidate.ProjectPackageId) ||
                string.IsNullOrWhiteSpace(fileName) || fileName != candidate.FileName ||
                candidate.Size is <= 0 or > 8L * 1024 * 1024 * 1024 ||
                candidate.Sha256.Length != 64 || candidate.Sha256.Any(character => !Uri.IsHexDigit(character)) ||
                candidate.ObjectName != $"sha256/{candidate.Sha256}/{candidate.FileName}")
                throw new ValidationException("Native promotion gate candidate manifest entry is invalid.");
        }
        return manifest;
    }

    public static void RecordPrepared(NativePromotionGate gate, PromotionGatePrepared prepared)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(prepared);
        var hash = (prepared.BundleSha256 ?? string.Empty).ToLowerInvariant();
        var expectedObject = $"promotion-gates/{gate.Id:N}/sha256/{hash}/input.tar";
        if (prepared.PromotionSetId != gate.Id || prepared.RepositoryId != gate.RepositoryId ||
            prepared.CandidateManifestSha256 != gate.CandidateManifestSha256 ||
            prepared.BundleObjectName != expectedObject || hash.Length != 64 ||
            hash.Any(character => !Uri.IsHexDigit(character)) ||
            prepared.BundleSize is <= 0 or > 8L * 1024 * 1024 * 1024 ||
            prepared.PreparedAt.Kind != DateTimeKind.Utc)
            throw new ValidationException("Prepared promotion gate bundle identity is invalid.");
        if (gate.BundleObjectName is not null)
        {
            if (gate.BundleObjectName == expectedObject && gate.BundleSha256 == hash &&
                gate.BundleSize == prepared.BundleSize)
                return;
            throw new ConflictException("Prepared promotion gate bundle identity changed.");
        }
        if (gate.Status != NativePromotionGateStatus.Pending)
            throw new ConflictException("Native promotion gate no longer accepts bundle preparation.");
        gate.BundleObjectName = expectedObject;
        gate.BundleSha256 = hash;
        gate.BundleSize = prepared.BundleSize;
        gate.BundlePreparedAt = prepared.PreparedAt;
        gate.UpdatedAt = prepared.PreparedAt;
    }

    private static NativePromotionGateCandidate CreateCandidate(
        BuildJob job,
        BuildArtifact artifact,
        Guid deliveryId,
        Guid promotionSetId,
        Guid repositoryId)
    {
        if (job.ProjectWebhookDeliveryId != deliveryId || string.IsNullOrWhiteSpace(job.ProjectPackageId) ||
            artifact.CandidatePackageId is null || artifact.PromotionSetId != promotionSetId ||
            artifact.CandidateRepositoryId != repositoryId || artifact.CandidateStagedAt is null ||
            artifact.FileSize is <= 0 or > 8L * 1024 * 1024 * 1024 ||
            artifact.HashSha256 is not { Length: 64 } hash ||
            hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ValidationException("Native promotion gate candidate provenance is incomplete.");
        }
        var fileName = Path.GetFileName(artifact.FileName);
        var normalizedHash = hash.ToLowerInvariant();
        var expectedObject = $"sha256/{normalizedHash}/{fileName}";
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(artifact.StoragePath, expectedObject, StringComparison.Ordinal))
        {
            throw new ValidationException("Native promotion gate candidate object identity is invalid.");
        }
        return new NativePromotionGateCandidate(
            artifact.Id, artifact.CandidatePackageId.Value, job.ProjectPackageId,
            fileName, expectedObject, artifact.FileSize, normalizedHash);
    }

    private static string NormalizeDigest(string value)
    {
        var digest = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest[7..].Any(character => !Uri.IsHexDigit(character)))
            throw new ValidationException("Native promotion gate runner digest is invalid.");
        return digest;
    }
}
