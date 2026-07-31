using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Models;

public class Package
{
    public Guid Id { get; set; }
    public Guid RepositoryId { get; set; }
    public Guid? PromotionSetId { get; set; }
    public Guid? ArtifactId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Release { get; set; } = string.Empty;
    public string Arch { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string? HashSha256 { get; set; }
    public string? PgpSignature { get; set; }
    public string? SigningKeyFingerprint { get; set; }
    public string? CandidateObjectName { get; set; }
    public string Status { get; set; } = "Ready";
    public ScanStatus CveScanStatus { get; set; } = ScanStatus.Pending;
    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;
    public string PublishedBy { get; set; } = string.Empty;

    public PackageRepository? Repository { get; set; }
    public RepositoryPromotionSet? PromotionSet { get; set; }
}
