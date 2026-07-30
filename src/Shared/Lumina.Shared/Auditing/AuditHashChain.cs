using System.Security.Cryptography;
using System.Text;
using Lumina.Shared.Models;

namespace Lumina.Shared.Auditing;

public static class AuditHashChain
{
    public const string GenesisHash =
        "0000000000000000000000000000000000000000000000000000000000000000";

    public static string Compute(AuditLog entry)
    {
        var canonical = string.Join('\n',
            entry.PreviousHash,
            entry.Id.ToString("D"),
            CanonicalTimestamp(entry.Timestamp),
            entry.Action,
            entry.EntityType,
            entry.EntityId,
            entry.PerformedBy,
            entry.Details,
            entry.IpAddress ?? string.Empty,
            entry.CorrelationId,
            entry.Phase,
            entry.StatusCode?.ToString() ?? string.Empty);

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static (bool Valid, long? BrokenSequence) Verify(
        IEnumerable<AuditLog> entries)
    {
        var previousHash = GenesisHash;
        foreach (var entry in entries.OrderBy(item => item.Sequence))
        {
            if (!VerifyEntry(entry, previousHash))
            {
                return (false, entry.Sequence);
            }

            previousHash = entry.EntryHash;
        }

        return (true, null);
    }

    public static bool VerifyEntry(AuditLog entry, string expectedPreviousHash)
    {
        return FixedTimeEquals(entry.PreviousHash, expectedPreviousHash) &&
            FixedTimeEquals(entry.EntryHash, Compute(entry));
    }

    private static bool FixedTimeEquals(string actual, string expected)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual),
            Encoding.ASCII.GetBytes(expected));
    }

    private static string CanonicalTimestamp(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        var microsecondTicks = utc.Ticks -
            utc.Ticks % TimeSpan.TicksPerMicrosecond;
        return new DateTimeOffset(microsecondTicks, TimeSpan.Zero).ToString("O");
    }
}
