using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.DTOs;

// Generic API Response
public record ApiResponse<T>(bool Success, T? Data, string? Error, string? Message);

// Pipeline Responses
public record PipelineResponse(Guid Id, string Name, string Description, PipelineStatus Status, List<PipelineStepResponse> Steps, string CreatedBy, DateTime CreatedAt, DateTime UpdatedAt, List<string> Tags, string? GitRepoUrl, string? GitBranch, string? SpecPath, string? WebhookUrl, string? BuildImage, string? GitUsername, bool HasGitToken, string? SpecContent = null, string TargetDistribution = "fedora", string TargetRelease = "44", string TargetArchitecture = "aarch64", string BuildProfile = "fedora-44-aarch64", List<string>? TriggerPaths = null);

public record PipelineStepResponse(Guid Id, StepType Type, string Name, int Order, Dictionary<string, string> Configuration);

public record PipelineListResponse(List<PipelineSummaryResponse> Pipelines, int TotalCount, int Page, int PageSize);

public record PipelineSummaryResponse(Guid Id, string Name, string Description, PipelineStatus Status, string CreatedBy, DateTime CreatedAt, int StepCount, string? GitRepoUrl = null, string? GitBranch = null);

// Build project responses
public record BuildProjectResponse(
    Guid Id,
    string Name,
    string GitRepoUrl,
    string GitBranch,
    string ManifestPath,
    bool IsActive,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string WebhookUrl,
    bool HasWebhookSecret,
    string? GitUsername,
    bool HasGitToken);

public record BuildProjectListResponse(
    List<BuildProjectResponse> Projects,
    int TotalCount,
    int Page,
    int PageSize);

// Build Responses
public record BuildJobResponse(Guid Id, Guid PipelineId, BuildStatus Status, string SpecName, string? ContainerId, string Logs, DateTime CreatedAt, DateTime? StartedAt, DateTime? CompletedAt, string TriggeredBy, List<BuildArtifactResponse> Artifacts, string? SourceUrl = null, string? CommitSha = null, string? Branch = null, string? CommitMessage = null, string? CommitAuthor = null, List<BuildStepRunResponse>? StepRuns = null, string? TargetDistribution = null, string? TargetRelease = null, string? TargetArchitecture = null, string? BuildProfile = null, string? RunnerImageReference = null, string? RunnerImageDigest = null);

public record BuildArtifactResponse(Guid Id, string FileName, long FileSize, string? HashSha256, string? HashMd5, string? SigningKeyFingerprint, DateTime? SignedAt, ScanStatus CveScanStatus, Guid? PublishedRepositoryId = null, DateTime? PublishedAt = null);

public record BuildStepRunResponse(Guid Id, StepType Type, string Name, int Order, StepStatus Status, DateTime? StartedAt, DateTime? CompletedAt, string? Error);

public record BuildListResponse(List<BuildJobSummaryResponse> Builds, int TotalCount, int Page, int PageSize);
public record BuildStatsResponse(int TotalCount, int SuccessfulCount, int FailedCount);

public record BuildJobSummaryResponse(Guid Id, Guid PipelineId, BuildStatus Status, string SpecName, DateTime CreatedAt, string TriggeredBy, string? CommitSha = null, string? Branch = null);

// Security Responses
public record SecurityKeyResponse(Guid Id, string KeyId, string KeyName, string PublicKey, bool IsActive, DateTime CreatedAt, DateTime? ExpiresAt, string CreatedBy);

public record VerifySignatureResponse(Guid ArtifactId, bool IsValid, DateTime VerifiedAt);

public record HashResponse(Guid ArtifactId, string HashSha256, string HashSha1, string HashMd5, DateTime ComputedAt);

public record HashListResponse(List<HashResponse> Hashes, int TotalCount, int Page, int PageSize);

// Scanner Responses

// Scan summary for paginated lists (used by ScannerService)
public record ScanSummaryResponse(Guid Id, Guid ArtifactId, string ScannerType, ScanStatus Status, int TotalVulnerabilities, int CriticalCount, int HighCount, DateTime CreatedAt, DateTime? CompletedAt);

public record ScanPaginatedResponse(List<ScanSummaryResponse> Scans, int TotalCount, int Page, int PageSize);

// Repository Responses
public record RepositoryResponse(Guid Id, string Name, string DisplayName, string BasePath, string Arch, string Distribution, bool IsActive, DateTime CreatedAt, int PackageCount);

public record PackageResponse(Guid Id, Guid RepositoryId, string Name, string Version, string Release, string Arch, string FileName, long FileSize, string? HashSha256, ScanStatus CveScanStatus, DateTime PublishedAt, string PublishedBy, string? PgpSignature = null, string? SigningKeyFingerprint = null, string Status = "Ready")
{
    /// <summary>
    /// Maps a <see cref="Package"/> entity to this response DTO. Centralizing the
    /// projection keeps the three controller call sites (publish, upload, list)
    /// in sync and ensures internal-only fields (<c>ArtifactId</c>,
    /// <c>StoragePath</c>, the <c>Repository</c> navigation) are never serialized.
    /// </summary>
    public static PackageResponse From(Package p) => new(
        p.Id, p.RepositoryId, p.Name, p.Version, p.Release, p.Arch, p.FileName,
        p.FileSize, p.HashSha256, p.CveScanStatus, p.PublishedAt, p.PublishedBy, p.PgpSignature, p.SigningKeyFingerprint, p.Status);
}

public record RepositoryListResponse(List<RepositoryResponse> Repositories, int TotalCount, int Page, int PageSize);

// Audit Responses
public record AuditLogResponse(
    Guid Id,
    long Sequence,
    string Action,
    string EntityType,
    string EntityId,
    string PerformedBy,
    DateTimeOffset Timestamp,
    string Details,
    string? IpAddress,
    string CorrelationId,
    string Phase,
    int? StatusCode,
    string PreviousHash,
    string EntryHash);

public record AuditLogListResponse(List<AuditLogResponse> Logs, int TotalCount, int Page, int PageSize);

public record AuditIntegrityResponse(bool Valid, long EntryCount, long? BrokenSequence);

// Dashboard Responses
public record DashboardStatsResponse(int TotalPipelines, int ActiveBuilds, int CompletedToday, int FailedToday, int VulnerablePackages, int TotalPackages);

public record BuildQueueResponse(List<BuildJobSummaryResponse> Queued, List<BuildJobSummaryResponse> Running, int QueuedCount, int RunningCount);

// Source Responses
public record SourcePackageResponse(
    Guid PackageId,
    string PackageName,
    int Revision,
    bool IsEnabled,
    string SourceUrl,
    SourceType SourceType,
    string? SourceBranch,
    string? ExpectedSha256,
    string? SpecPath,
    string? BuildImage,
    SourceStatus Status,
    string? ErrorMessage,
    long? FileSize,
    string? HashSha256,
    DateTime? LastFetchedAt,
    string? ResolvedRevision = null,
    string? ResolvedUrl = null);

public record SourceListResponse(List<SourcePackageResponse> Packages, int TotalCount);

public record SourceFetchResponse(
    Guid JobId,
    string PackageName,
    SourceStatus Status,
    string? ErrorMessage,
    string? ResolvedRevision = null,
    string? ResolvedUrl = null);

public record SourcePackageMutationResponse(
    SourcePackageResponse Package,
    SourceFetchResponse? Fetch);

// Pre-signed download URL for a fetched source archive. Replaces the ad-hoc
// `new { url, packageName, fileSize, hashSha256 }` shape that SourceController
// used to return, so every source JSON response shares the ApiResponse<T> envelope.
public record SourceDownloadResponse(string Url, string PackageName, long? FileSize, string? HashSha256);

// Uploaded Extra Source Responses (pipeline-level & build-level)
public record UploadedSourceResponse(string FileName, string Path, long FileSize, DateTime UploadedAt, string? SubFolder);
