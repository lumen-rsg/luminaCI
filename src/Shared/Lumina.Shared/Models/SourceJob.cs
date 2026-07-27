using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Models;

public class SourceJob
{
    public Guid Id { get; set; }
    public Guid? PackageRevisionId { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public SourceType SourceType { get; set; }
    public string? SourceBranch { get; set; }
    public string? ExpectedSha256 { get; set; }
    public string? ResolvedRevision { get; set; }
    public string? ResolvedUrl { get; set; }
    public SourceStatus Status { get; set; } = SourceStatus.Pending;
    public string? StoragePath { get; set; }
    public string? ErrorMessage { get; set; }
    public long? FileSize { get; set; }
    public string? HashSha256 { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public DateTime? HeartbeatAt { get; set; }
    public bool CancellationRequested { get; set; }
    public DateTime? FetchStartedAt { get; set; }
    public DateTime? FetchCompletedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public PackageRevision? PackageRevision { get; set; }
}
