namespace Lumina.Shared.Security;

/// <summary>
/// At-rest protection for secret string fields (e.g. <c>Pipeline.WebhookSecret</c>,
/// <c>Pipeline.GitToken</c>). Implementations encrypt on write and decrypt on read
/// so that persisted storage only ever holds ciphertext, while in-memory entities
/// hold the plaintext the application needs to use.
/// </summary>
/// <remarks>
/// <see cref="Unprotect"/> must transparently pass through legacy plaintext
/// (values written before encryption was enabled) so existing rows keep working
/// and are re-encrypted as ciphertext on their next write.
/// </remarks>
public interface ISecretProtector
{
    /// <summary>
    /// Encrypts a secret. Returns <c>null</c> for <c>null</c> and <c>""</c> for
    /// <c>""</c> so nullable/empty columns are preserved as-is.
    /// </summary>
    string? Protect(string? plaintext);

    /// <summary>
    /// Decrypts a value previously returned by <see cref="Protect"/>. Values that
    /// are not recognized as protected ciphertext (legacy plaintext) are returned
    /// unchanged so rows written before encryption keep functioning.
    /// </summary>
    string? Unprotect(string? value);
}
