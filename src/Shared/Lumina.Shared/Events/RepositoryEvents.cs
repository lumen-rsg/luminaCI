namespace Lumina.Shared.Events;

// Repository Events
public record PackagePublishRequested(
    Guid ArtifactId,
    Guid RepositoryId,
    string ExpectedSha256,
    string PublishedBy,
    DateTime RequestedAt,
    Guid PromotionSetId = default,
    string PromotionGroup = "",
    string TargetArchitecture = "",
    string? GateRunnerImageDigest = null);

public record PackageCandidateRequested(
    Guid ArtifactId,
    Guid RepositoryId,
    string ExpectedSha256,
    string StagedBy,
    DateTime RequestedAt,
    Guid PromotionSetId,
    string PromotionGroup,
    string ProjectPackageId,
    string TargetArchitecture,
    string GateRunnerImageDigest);

public record PackageCandidateStaged(
    Guid ArtifactId,
    Guid RepositoryId,
    Guid CandidatePackageId,
    Guid PromotionSetId,
    DateTime StagedAt);

public record PromotionGateCandidateInput(
    Guid ArtifactId,
    Guid CandidatePackageId,
    string ProjectPackageId,
    string FileName,
    string ObjectName,
    long Size,
    string Sha256);

public record PromotionGatePreparationRequested(
    Guid PromotionSetId,
    Guid RepositoryId,
    string CandidateManifestSha256,
    IReadOnlyList<PromotionGateCandidateInput> Candidates,
    DateTime RequestedAt);

public record PromotionGatePrepared(
    Guid PromotionSetId,
    Guid RepositoryId,
    string CandidateManifestSha256,
    string BundleObjectName,
    string BundleSha256,
    long BundleSize,
    DateTime PreparedAt);

public record PromotionGateStarted(
    Guid PromotionSetId,
    Guid RepositoryId,
    string KubernetesJobName,
    string KubernetesJobUid,
    DateTime StartedAt);

public record PromotionGateCompleted(
    Guid PromotionSetId,
    Guid RepositoryId,
    string KubernetesJobName,
    string KubernetesJobUid,
    string RunnerImageDigest,
    bool Succeeded,
    string? ResultSha256,
    string? FailureReason,
    DateTime CompletedAt);

public record PackagePublished(Guid ArtifactId, Guid RepositoryId, Guid PackageId, DateTime PublishedAt);

public record RepositoryUpdated(Guid RepositoryId, string RepositoryName, DateTime UpdatedAt);

public record RepositorySyncRequested(Guid RepositoryId, DateTime RequestedAt);
