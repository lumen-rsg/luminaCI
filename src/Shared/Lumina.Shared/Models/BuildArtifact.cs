using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Models;

public class BuildArtifact
{
    public Guid Id { get; set; }
    public Guid BuildJobId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string? HashSha256 { get; set; }
    public string? HashSha512 { get; set; }
    public string? HashMd5 { get; set; }
    public string? SigningKeyFingerprint { get; set; }
    public DateTime? SignedAt { get; set; }
    public Guid? PublishedRepositoryId { get; set; }
    public DateTime? PublishedAt { get; set; }
    public ScanStatus CveScanStatus { get; set; } = ScanStatus.Pending;
    public string? SourceStoragePath { get; set; }
    public string? RpmNevra { get; set; }
    public string StoragePath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public BuildJob? BuildJob { get; set; }
}
