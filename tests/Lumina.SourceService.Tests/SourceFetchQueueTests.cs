using Lumina.SourceService.Data;
using Lumina.SourceService.Services;
using Lumina.Shared.Events;
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

    [Fact]
    public async Task EnqueueRepositorySnapshot_IsIdempotentByRequestId()
    {
        await using var db = CreateDb();
        var queue = CreateQueue(db);
        var request = SnapshotRequest();

        var first = await queue.EnqueueRepositorySnapshotAsync(request);
        var second = await queue.EnqueueRepositorySnapshotAsync(request);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(request.RequestId, first.SnapshotRequestId);
        Assert.Equal(request.ProjectId, first.SnapshotProjectId);
        Assert.Equal(request.CommitSha, first.SourceBranch);
        Assert.Equal(request.ManifestPath, first.SnapshotManifestPath);
        Assert.Equal(SourceType.Git, first.SourceType);
        Assert.Equal(1, await db.SourceJobs.CountAsync());
    }

    [Theory]
    [InlineData("main")]
    [InlineData("abc123")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
    public async Task EnqueueRepositorySnapshot_RejectsNonCommitReference(string commit)
    {
        await using var db = CreateDb();
        var queue = CreateQueue(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            queue.EnqueueRepositorySnapshotAsync(SnapshotRequest() with { CommitSha = commit }));
    }

    [Fact]
    public async Task EnqueueRepositorySnapshot_RejectsUnsafeManifestPath()
    {
        await using var db = CreateDb();
        var queue = CreateQueue(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            queue.EnqueueRepositorySnapshotAsync(
                SnapshotRequest() with { ManifestPath = "../packages.yaml" }));
    }

    private static RepositorySnapshotRequested SnapshotRequest() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "https://github.com/lumina/packages.git",
        new string('a', 40),
        ".lumina/packages.yaml",
        DateTime.UtcNow);

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
