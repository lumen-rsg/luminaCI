namespace Lumina.Shared.Models;

public class SigningRequest
{
    public Guid Id { get; set; }
    public Guid BuildJobId { get; set; }
    public Guid ArtifactId { get; set; }
    public string ArtifactPath { get; set; } = string.Empty;
    public Guid KeyId { get; set; }
    public string KeyFingerprint { get; set; } = string.Empty;
    public string ExpectedSha256 { get; set; } = string.Empty;
    public string? SignedSha256 { get; set; }
    public long? SignedFileSize { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Signed, Failed
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

}
