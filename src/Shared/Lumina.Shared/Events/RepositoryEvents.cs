namespace Lumina.Shared.Events;

// Repository Events
public record PackagePublishRequested(
    Guid ArtifactId,
    Guid RepositoryId,
    string PublishedBy,
    DateTime RequestedAt);

public record PackagePublished(Guid ArtifactId, Guid RepositoryId, Guid PackageId, DateTime PublishedAt);

public record RepositoryUpdated(Guid RepositoryId, string RepositoryName, DateTime UpdatedAt);

public record RepositorySyncRequested(Guid RepositoryId, DateTime RequestedAt);
