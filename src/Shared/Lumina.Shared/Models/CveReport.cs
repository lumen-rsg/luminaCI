using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Models;

public class CveReport
{
    public Guid Id { get; set; }
    public Guid ArtifactId { get; set; }
    public string ScannerType { get; set; } = string.Empty; // Trivy, Grype
    public ScanStatus Status { get; set; } = ScanStatus.Pending;
    public List<Vulnerability> Vulnerabilities { get; set; } = [];
    public DateTime ScannedAt { get; set; } = DateTime.UtcNow;
    public int CriticalCount { get; set; }
    public int HighCount { get; set; }
    public int MediumCount { get; set; }
    public int LowCount { get; set; }

    /// <summary>When this report record was created (start of scan).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the scan completed or failed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Human-readable summary of scan results.</summary>
    public string? Summary { get; set; }

    /// <summary>Raw JSON output from the scanner.</summary>
    public string? RawOutput { get; set; }

    public BuildArtifact? Artifact { get; set; }
}