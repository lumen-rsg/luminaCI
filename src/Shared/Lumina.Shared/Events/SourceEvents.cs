namespace Lumina.Shared.Events;

/// <summary>
/// Requests a credential-free immutable snapshot of one exact repository
/// commit. SourceService validates and fetches the source; BuildService never
/// clones repository content in the public webhook request.
/// </summary>
public record RepositorySnapshotRequested(
    Guid RequestId,
    Guid ProjectId,
    string RepositoryUrl,
    string CommitSha,
    string ManifestPath,
    DateTime RequestedAt);

/// <summary>
/// Reports the content-addressed snapshot created for an exact commit. Only
/// metadata crosses RabbitMQ; the archive remains in object storage.
/// </summary>
public record RepositorySnapshotReady(
    Guid RequestId,
    Guid ProjectId,
    Guid SourceJobId,
    string StoragePath,
    string HashSha256,
    long FileSize,
    string ResolvedCommit,
    string ManifestPath,
    DateTime CompletedAt);

/// <summary>Reports a terminal snapshot failure without exposing raw tool output.</summary>
public record RepositorySnapshotFailed(
    Guid RequestId,
    Guid ProjectId,
    Guid SourceJobId,
    string Reason,
    DateTime FailedAt);
