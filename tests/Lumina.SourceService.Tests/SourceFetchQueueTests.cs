using Lumina.SourceService.Data;
using Lumina.SourceService.Services;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumina.SourceService.Tests;

public sealed class SourceFetchQueueTests
{
    [Fact]
    public async Task Enqueue_CreatesDistinctPendingAttemptsAndPreservesHistory()
    {
        await using var db = CreateDb();
        var queue = CreateQueue(db);

        var first = await queue.EnqueueAsync(
            "demo", "https://example.com/demo.tar.gz", SourceType.Tar,
            expectedSha256: new string('a', 64));
        var second = await queue.EnqueueAsync(
            "demo", "https://example.com/demo.tar.gz", SourceType.Tar);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(SourceStatus.Pending, first.Status);
        Assert.Equal(SourceStatus.Pending, second.Status);
        Assert.Equal(3, first.MaxRetries);
        Assert.Equal(new string('a', 64), first.ExpectedSha256);
        Assert.Equal(2, await db.SourceJobs.CountAsync(j => j.PackageName == "demo"));
    }

    [Fact]
    public async Task Enqueue_ClampsCallerRetryBudget()
    {
        await using var db = CreateDb();
        var queue = CreateQueue(db);

        var job = await queue.EnqueueAsync(
            "demo", "https://example.com/demo.tar.gz", SourceType.Tar, maxRetries: 99);

        Assert.Equal(10, job.MaxRetries);
    }

    [Fact]
    public async Task Enqueue_AllowsCallerToDisableRetries()
    {
        await using var db = CreateDb();
        var queue = CreateQueue(db);

        var job = await queue.EnqueueAsync(
            "demo", "https://example.com/demo.tar.gz", SourceType.Tar, maxRetries: 0);

        Assert.Equal(0, job.MaxRetries);
    }

    private static SourceDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<SourceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new SourceDbContext(options);
    }

    private static SourceFetchQueue CreateQueue(SourceDbContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Source:MaxRetries"] = "3"
            })
            .Build();
        return new SourceFetchQueue(
            db, configuration, NullLogger<SourceFetchQueue>.Instance);
    }
}
