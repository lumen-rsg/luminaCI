namespace Lumina.Shared.Models;

public class SigningRequest
{
    public Guid Id { get; set; }
    public Guid BuildJobId { get; set; }
    public Guid ArtifactId { get; set; }
    public string ArtifactPath { get; set; } = string.Empty;
    public string SignaturePath { get; set; } = string.Empty;
    public Guid KeyId { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Signed, Failed
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Transient — the PGP signature content, not persisted to DB.
    /// Used to pass signature from PgpSigningService to consumer.
    /// </summary>
    public string? SignatureContent { get; set; }
}
