using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Lumina.Shared.Security;

/// <summary>
/// <see cref="ISecretProtector"/> backed by AES-256-GCM, deriving its 32-byte key
/// from the <c>Secrets:MasterKey</c> configuration value (supplied via the
/// <c>SECRETS_MASTER_KEY</c> environment variable in production).
/// </summary>
/// <remarks>
/// <para>
/// Ciphertext layout (<see cref="TagPrefix"/> distinguishes protected values from
/// legacy plaintext): <c>"enc1:" + Base64(random 12-byte nonce | ciphertext | 16-byte tag)</c>.
/// A fresh nonce is generated for every write, so repeated values never collide.
/// </para>
/// <para>
/// <see cref="Unprotect"/> returns any value that lacks the prefix unchanged. That
/// keeps existing cleartext rows readable and lets them be rotated to ciphertext on
/// the next save without a dedicated migration step.
/// </para>
/// </remarks>
public sealed class AesSecretProtector : ISecretProtector
{
    /// <summary>
    /// Prefix marking a value as encrypted. Anything without it is treated as
    /// legacy plaintext by <see cref="Unprotect"/>.
    /// </summary>
    public const string TagPrefix = "enc1:";

    private const int NonceBytes = 12;   // AES-GCM recommended nonce size
    private const int TagBytes = 16;     // AES-GCM authentication tag
    private const int KeyBytes = 32;     // AES-256

    private readonly byte[] _key;
    private readonly ILogger<AesSecretProtector> _logger;

    public AesSecretProtector(IConfiguration configuration, ILogger<AesSecretProtector> logger)
    {
        var rawKey = configuration["Secrets:MasterKey"];
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            throw new InvalidOperationException(
                "Secrets:MasterKey is not configured. Set SECRETS_MASTER_KEY to a strong " +
                "random value (see deploy/.env.example) so pipeline secrets can be encrypted at rest.");
        }

        // SHA-256 derives a fixed-length 256-bit key from whatever the operator
        // supplies, so they are free to use a passphrase of arbitrary length
        // while we still get a cryptographically uniform key.
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        _logger = logger;
    }

    public string? Protect(string? plaintext)
    {
        // Preserve null/empty as-is so nullable columns keep their semantics.
        if (string.IsNullOrEmpty(plaintext)) return plaintext;

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagBytes];

        using var gcm = new AesGcm(_key, TagBytes);
        gcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Pack nonce | ciphertext | tag together, then Base64 the whole blob.
        var packed = new byte[NonceBytes + ciphertext.Length + TagBytes];
        Buffer.BlockCopy(nonce, 0, packed, 0, NonceBytes);
        Buffer.BlockCopy(ciphertext, 0, packed, NonceBytes, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, packed, NonceBytes + ciphertext.Length, TagBytes);

        return TagPrefix + Convert.ToBase64String(packed);
    }

    public string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        // Legacy plaintext (written before at-rest encryption): pass through
        // untouched. It will be rotated to ciphertext on the next save.
        if (!value.StartsWith(TagPrefix, StringComparison.Ordinal)) return value;

        try
        {
            var packed = Convert.FromBase64String(value[TagPrefix.Length..]);
            if (packed.Length < NonceBytes + TagBytes)
                throw new FormatException("Protected value is too short");

            var nonce = packed[..NonceBytes];
            var tag = packed[^TagBytes..];
            var ciphertext = packed[NonceBytes..^TagBytes];
            var plaintext = new byte[ciphertext.Length];

            using var gcm = new AesGcm(_key, TagBytes);
            gcm.Decrypt(nonce, ciphertext, tag, plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // A bad value here most likely means the MasterKey was rotated/changed
            // since the secret was written. Fail loud rather than silently serving
            // ciphertext as if it were plaintext.
            _logger.LogError(ex, "Failed to decrypt a protected secret — was Secrets:MasterKey rotated?");
            throw new InvalidOperationException(
                "Could not decrypt a stored secret. Check that Secrets:MasterKey matches the key " +
                "used when the value was written.", ex);
        }
    }
}
