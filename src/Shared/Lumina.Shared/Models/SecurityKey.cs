namespace Lumina.Shared.Models;

public class SecurityKey
{
    public Guid Id { get; set; }
    /// <summary>The full, uppercase OpenPGP fingerprint used by rpmsign.</summary>
    public string KeyId { get; set; } = string.Empty;
    public string KeyName { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
