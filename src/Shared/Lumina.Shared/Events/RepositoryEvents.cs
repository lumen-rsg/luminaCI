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

public record PackagePublished(Guid ArtifactId, Guid RepositoryId, Guid PackageId, DateTime PublishedAt);

public record RepositoryUpdated(Guid RepositoryId, string RepositoryName, DateTime UpdatedAt);

public record RepositorySyncRequested(Guid RepositoryId, DateTime RequestedAt);
