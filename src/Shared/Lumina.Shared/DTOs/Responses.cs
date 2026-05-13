using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.DTOs;

// Generic API Response
public record ApiResponse<T>(bool Success, T? Data, string? Error, string? Message);

// Pipeline Responses
public record PipelineResponse(Guid Id, string Name, string Description, PipelineStatus Status, List<PipelineStepResponse> Steps, string CreatedBy, DateTime CreatedAt, DateTime UpdatedAt, List<string> Tags, string? GitRepoUrl, string? GitBranch, string? SpecPath, string? WebhookUrl, string? BuildImage, string? GitUsername, bool HasGitToken);

public record PipelineStepResponse(Guid Id, StepType Type, string Name, int Order, StepStatus Status, Dictionary<string, string> Configuration);

public record PipelineListResponse(List<PipelineSummaryResponse> Pipelines, int TotalCount, int Page, int PageSize);

public record PipelineSummaryResponse(Guid Id, string Name, string Description, PipelineStatus Status, string CreatedBy, DateTime CreatedAt, int StepCount, string? GitRepoUrl = null, string? GitBranch = null);

// Build Responses
public record BuildJobResponse(Guid Id, Guid PipelineId, BuildStatus Status, string SpecName, string? ContainerId, string Logs, DateTime CreatedAt, DateTime? StartedAt, DateTime? CompletedAt, string TriggeredBy, List<BuildArtifactResponse> Artifacts, string? SourceUrl = null);

public record BuildArtifactResponse(Guid Id, string FileName, long FileSize, string? HashSha256, string? HashMd5, string? PgpSignature, ScanStatus CveScanStatus);

public record BuildListResponse(List<BuildJobSummaryResponse> Builds, int TotalCount, int Page, int PageSize);

public record BuildJobSummaryResponse(Guid Id, Guid PipelineId, BuildStatus Status, string SpecName, DateTime CreatedAt, string TriggeredBy);

// Security Responses
public record SecurityKeyResponse(Guid Id, string KeyId, string KeyName, string PublicKey, bool IsActive, DateTime CreatedAt, DateTime? ExpiresAt, string CreatedBy);

public record SignPackageResponse(Guid ArtifactId, string PgpSignature, DateTime SignedAt);

public record VerifySignatureResponse(Guid ArtifactId, bool IsValid, DateTime VerifiedAt);

public record HashResponse(Guid ArtifactId, string HashSha256, string HashSha512, string HashMd5, DateTime ComputedAt);

public record HashListResponse(List<HashResponse> Hashes, int TotalCount, int Page, int PageSize);

// Scanner Responses
public record ScanResponse(Guid Id, Guid ArtifactId, string ScannerType, ScanStatus Status, DateTime StartedAt, DateTime? CompletedAt);

public record ScanListResponse(List<ScanResponse> Scans, int TotalCount, int Page, int PageSize);

public record CveReportResponse(Guid Id, Guid ArtifactId, string ScannerType, ScanStatus Status, List<VulnerabilityResponse> Vulnerabilities, DateTime ScannedAt, int CriticalCount, int HighCount, int MediumCount, int LowCount);

public record VulnerabilityResponse(string Id, string PackageName, string Title, string Description, string Severity, string? FixedVersion, string InstalledVersion);

public record CveReportListResponse(List<CveReportSummaryResponse> Reports, int TotalCount, int Page, int PageSize);

public record CveReportSummaryResponse(Guid Id, Guid ArtifactId, string ScannerType, ScanStatus Status, DateTime ScannedAt, int TotalVulnerabilities);

// Repository Responses
public record RepositoryResponse(Guid Id, string Name, string DisplayName, string BasePath, string Arch, string Distribution, bool IsActive, DateTime CreatedAt, int PackageCount);

public record PackageResponse(Guid Id, Guid RepositoryId, string Name, string Version, string Release, string Arch, string FileName, long FileSize, string? HashSha256, ScanStatus CveScanStatus, DateTime PublishedAt, string PublishedBy);

public record RepositoryListResponse(List<RepositoryResponse> Repositories, int TotalCount, int Page, int PageSize);

// Audit Responses
public record AuditLogResponse(Guid Id, string Action, string EntityType, string EntityId, string PerformedBy, DateTime Timestamp, string Details, string? IpAddress);

public record AuditLogListResponse(List<AuditLogResponse> Logs, int TotalCount, int Page, int PageSize);

// Dashboard Responses
public record DashboardStatsResponse(int TotalPipelines, int ActiveBuilds, int CompletedToday, int FailedToday, int VulnerablePackages, int TotalPackages);

public record BuildQueueResponse(List<BuildJobSummaryResponse> Queued, List<BuildJobSummaryResponse> Running, int QueuedCount, int RunningCount);