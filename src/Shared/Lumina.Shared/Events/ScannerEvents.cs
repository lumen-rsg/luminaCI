using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Events;

// Note: CveScanRequested and CveScanCompleted are defined in BuildEvents.cs

public record VulnerabilityFound(Guid ArtifactId, string CveId, string Severity, string PackageName, DateTime FoundAt);