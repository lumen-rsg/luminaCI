using System.Security.Cryptography;
using System.Text;
using Lumina.Shared.Errors;

namespace Lumina.Shared.Models;

/// <summary>
/// Produces the immutable identifier shared by every package in one promotion
/// group for a repository delivery. Manual builds use their build ID as the
/// owner and consequently remain isolated from every other build.
/// </summary>
public static class PromotionSetIdentity
{
    public static Guid Create(Guid ownerId, string promotionGroup)
    {
        if (ownerId == Guid.Empty)
            throw new ValidationException("Promotion-set owner cannot be empty.");

        var normalizedGroup = NormalizeGroup(promotionGroup);
        Span<byte> owner = stackalloc byte[16];
        ownerId.TryWriteBytes(owner, bigEndian: true, out _);
        var group = Encoding.UTF8.GetBytes(normalizedGroup);
        var input = new byte[owner.Length + group.Length];
        owner.CopyTo(input);
        group.CopyTo(input, owner.Length);

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        Span<byte> identity = stackalloc byte[16];
        digest[..16].CopyTo(identity);

        // RFC 9562 UUIDv8: application-defined deterministic payload.
        identity[6] = (byte)((identity[6] & 0x0f) | 0x80);
        identity[8] = (byte)((identity[8] & 0x3f) | 0x80);
        return new Guid(identity, bigEndian: true);
    }

    public static string NormalizeGroup(string promotionGroup)
    {
        var normalized = (promotionGroup ?? string.Empty).Trim();
        if (normalized.Length is < 1 or > 128 ||
            normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new ValidationException(
                "Promotion group must contain 1-128 ASCII letters, digits, '.', '_' or '-'.");
        }
        return normalized;
    }
}
