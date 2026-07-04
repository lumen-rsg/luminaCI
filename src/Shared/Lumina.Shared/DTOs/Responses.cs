using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.DTOs;

// Generic API Response
public record ApiResponse<T>(bool Success, T? Data, string? Error, string? Message);

// Pipeline Responses
public record PipelineResponse(Guid Id, string Name, string Description, PipelineStatus Status, List<PipelineStepResponse> Steps, string CreatedBy, DateTime CreatedAt, DateTime UpdatedAt, List<string> Tags, string? GitRepoUrl, string? GitBranch, string? SpecPath, string? WebhookUrl, string? BuildImage, string? GitUsername, bool HasGitToken, string? SpecContent = null);

public record PipelineStepResponse(Guid Id, StepType Type, string Name, int Order, StepStatus Status, Dictionary<string, string> Configuration);

public record PipelineListResponse(List<PipelineSummaryResponse> Pipelines, int TotalCount, int Page, int PageSize);

public record PipelineSummaryResponse(Guid Id, string Name, string Description, PipelineStatus Status, string CreatedBy, DateTime CreatedAt, int StepCount, string? GitRepoUrl = null, string? GitBranch = null);

// Build Responses
public record BuildJobResponse(Guid Id, Guid PipelineId, BuildStatus Status, string SpecName, string? ContainerId, string Logs, DateTime CreatedAt, DateTime? StartedAt, DateTime? CompletedAt, string TriggeredBy, List<BuildArtifactResponse> Artifacts, string? SourceUrl = null, string? CommitSha = null, string? Branch = null, string? CommitMessage = null, string? CommitAuthor = null);

public record BuildArtifactResponse(Guid Id, string FileName, long FileSize, string? HashSha256, string? HashMd5, string? PgpSignature, ScanStatus CveScanStatus);

public record BuildListResponse(List<BuildJobSummaryResponse> Builds, int TotalCount, int Page, int PageSize);

public record BuildJobSummaryResponse(Guid Id, Guid PipelineId, BuildStatus Status, string SpecName, DateTime CreatedAt, string TriggeredBy, string? CommitSha = null, string? Branch = null);

// Security Responses
public record SecurityKeyResponse(Guid Id, string KeyId, string KeyName, string PublicKey, bool IsActive, DateTime CreatedAt, DateTime? ExpiresAt, string CreatedBy);

public record SignPackageResponse(Guid ArtifactId, string PgpSignature, DateTime SignedAt);

public record VerifySignatureResponse(Guid ArtifactId, bool IsValid, DateTime VerifiedAt);

public record HashResponse(Guid ArtifactId, string HashSha256, string HashSha1, string HashMd5, DateTime ComputedAt);

public record HashListResponse(List<HashResponse> Hashes, int TotalCount, int Page, int PageSize);

// Scanner Responses

// Scan summary for paginated lists (used by ScannerService)
public record ScanSummaryResponse(Guid Id, Guid ArtifactId, string ScannerType, ScanStatus Status, int TotalVulnerabilities, int CriticalCount, int HighCount, DateTime CreatedAt, DateTime? CompletedAt);

public record ScanPaginatedResponse(List<ScanSummaryResponse> Scans, int TotalCount, int Page, int PageSize);

// Repository Responses
public record RepositoryResponse(Guid Id, string Name, string DisplayName, string BasePath, string Arch, string Distribution, bool IsActive, DateTime CreatedAt, int PackageCount);

public record PackageResponse(Guid Id, Guid RepositoryId, string Name, string Version, string Release, string Arch, string FileName, long FileSize, string? HashSha256, ScanStatus CveScanStatus, DateTime PublishedAt, string PublishedBy, string? PgpSignature = null)
{
    /// <summary>
    /// Maps a <see cref="Package"/> entity to this response DTO. Centralizing the
    /// projection keeps the three controller call sites (publish, upload, list)
    /// in sync and ensures internal-only fields (<c>ArtifactId</c>,
    /// <c>StoragePath</c>, the <c>Repository</c> navigation) are never serialized.
    /// </summary>
    public static PackageResponse From(Package p) => new(
        p.Id, p.RepositoryId, p.Name, p.Version, p.Release, p.Arch, p.FileName,
        p.FileSize, p.HashSha256, p.CveScanStatus, p.PublishedAt, p.PublishedBy, p.PgpSignature);
}

public record RepositoryListResponse(List<RepositoryResponse> Repositories, int TotalCount, int Page, int PageSize);

// Audit Responses
public record AuditLogResponse(Guid Id, string Action, string EntityType, string EntityId, string PerformedBy, DateTime Timestamp, string Details, string? IpAddress);

public record AuditLogListResponse(List<AuditLogResponse> Logs, int TotalCount, int Page, int PageSize);

// Dashboard Responses
public record DashboardStatsResponse(int TotalPipelines, int ActiveBuilds, int CompletedToday, int FailedToday, int VulnerablePackages, int TotalPackages);

public record BuildQueueResponse(List<BuildJobSummaryResponse> Queued, List<BuildJobSummaryResponse> Running, int QueuedCount, int RunningCount);

// Source Responses
public record SourcePackageResponse(string PackageName, string SourceUrl, SourceType SourceType, string? SourceBranch, SourceStatus Status, string? ErrorMessage, long? FileSize, string? HashSha256, DateTime? LastFetchedAt);

public record SourceListResponse(List<SourcePackageResponse> Packages, int TotalCount);

public record SourceFetchResponse(Guid JobId, string PackageName, SourceStatus Status, string? ErrorMessage);

// Uploaded Extra Source Responses (pipeline-level & build-level)
public record UploadedSourceResponse(string FileName, string Path, long FileSize, DateTime UploadedAt, string? SubFolder);
