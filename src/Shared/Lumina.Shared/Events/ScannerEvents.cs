using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Events;

// Scanner Events
public record CveScanRequested(Guid ArtifactId, string FileName, string StoragePath, string ScannerType, DateTime RequestedAt);

public record CveScanCompleted(Guid ArtifactId, ScanStatus Status, int CriticalCount, int HighCount, int MediumCount, int LowCount, DateTime CompletedAt);

public record VulnerabilityFound(Guid ArtifactId, string CveId, string Severity, string PackageName, DateTime FoundAt);