using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Events;

// Build Events
public record BuildJobCreated(Guid BuildJobId, Guid PipelineId, string SpecName, string TriggeredBy, DateTime CreatedAt);

public record BuildJobStarted(Guid BuildJobId, string ContainerId, DateTime StartedAt);

public record BuildJobCompleted(Guid BuildJobId, BuildStatus Status, DateTime CompletedAt);

public record BuildJobFailed(Guid BuildJobId, string ErrorMessage, DateTime FailedAt);

/// <summary>
/// Sent by SourceService to trigger a build from conf.ini configuration.
/// Consumed by BuildService.
/// </summary>
public record BuildTriggerFromConfig(
    string PackageName,
    string SourceDir,
    string SpecContent,
    string SpecName,
    string? BuildImage,
    string TriggeredBy = "source-service");

/// <summary>
/// Sent by BuildService after a successful build to request CVE scanning of artifacts.
/// Consumed by ScannerService.
/// </summary>
public record CveScanRequested(Guid ArtifactId, string ArtifactPath, string FileName, string ScannerType, DateTime RequestedAt);

/// <summary>
/// Sent by ScannerService when CVE scan completes.
/// Consumed by BuildService to update artifact scan status.
/// </summary>
public record CveScanCompleted(Guid ArtifactId, ScanStatus Status, int CriticalCount, int HighCount, int MediumCount, int LowCount, DateTime CompletedAt);

/// <summary>
/// Sent by BuildService to request hash storage for an artifact.
/// Consumed by SecurityService.
/// </summary>
public record HashStoreRequested(Guid ArtifactId, string FileName, string Sha256, string Md5, long FileSize, DateTime RequestedAt);

/// <summary>
/// Sent by BuildService to request PGP signing of an artifact.
/// Consumed by SecurityService.
/// </summary>
public record PackageSigningRequested(Guid ArtifactId, string ArtifactPath, string FileName, Guid KeyId, DateTime RequestedAt);

/// <summary>
/// Sent by SecurityService when PGP signing completes.
/// Consumed by BuildService to update artifact signature.
/// </summary>
public record PackageSigned(Guid ArtifactId, string PgpSignature, DateTime SignedAt);