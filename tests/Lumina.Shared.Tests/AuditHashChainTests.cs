using Lumina.Shared.Auditing;
using Lumina.Shared.Models;
using Xunit;

namespace Lumina.Shared.Tests;

public sealed class AuditHashChainTests
{
    [Fact]
    public void ValidChainPassesIntegrityCheck()
    {
        var entries = BuildChain(3);

        var result = AuditHashChain.Verify(entries);

        Assert.True(result.Valid);
        Assert.Null(result.BrokenSequence);
    }

    [Fact]
    public void ModifiedEntryBreaksItsHash()
    {
        var entries = BuildChain(3);
        entries[1].Details = """{"changed":true}""";

        var result = AuditHashChain.Verify(entries);

        Assert.False(result.Valid);
        Assert.Equal(2, result.BrokenSequence);
    }

    [Fact]
    public void RemovedEntryBreaksTheFollowingLink()
    {
        var entries = BuildChain(3);
        entries.RemoveAt(1);

        var result = AuditHashChain.Verify(entries);

        Assert.False(result.Valid);
        Assert.Equal(3, result.BrokenSequence);
    }

    [Fact]
    public void SubMicrosecondTimestampPrecisionDoesNotChangeHash()
    {
        var entry = BuildChain(1)[0];
        var originalHash = entry.EntryHash;

        entry.Timestamp = entry.Timestamp.AddTicks(7);

        Assert.Equal(originalHash, AuditHashChain.Compute(entry));
    }

    private static List<AuditLog> BuildChain(int count)
    {
        var entries = new List<AuditLog>();
        var previousHash = AuditHashChain.GenesisHash;
        for (var index = 1; index <= count; index++)
        {
            var entry = new AuditLog
            {
                Id = Guid.CreateVersion7(),
                Sequence = index,
                Action = "http.post",
                EntityType = "pipelines",
                EntityId = index.ToString(),
                PerformedBy = "operator",
                Timestamp = DateTimeOffset.Parse(
                    $"2026-07-31T00:00:0{index}+00:00"),
                Details = """{"phase":"Completed"}""",
                IpAddress = "127.0.0.1",
                CorrelationId = $"correlation-{index}",
                Phase = "Completed",
                StatusCode = 200,
                PreviousHash = previousHash
            };
            entry.EntryHash = AuditHashChain.Compute(entry);
            entries.Add(entry);
            previousHash = entry.EntryHash;
        }
        return entries;
    }
}
