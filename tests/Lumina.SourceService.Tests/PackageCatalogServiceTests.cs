using Lumina.Shared.DTOs;
using Lumina.Shared.Models.Enums;
using Lumina.SourceService.Data;
using Lumina.SourceService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumina.SourceService.Tests;

public sealed class PackageCatalogServiceTests
{
    [Fact]
    public async Task Create_PersistsRevisionAndQueuesItsAutomaticFetch()
    {
        await using var db = CreateDb();
        var catalog = CreateCatalog(db);

        var (package, job) = await catalog.CreateAsync(
            GitRequest("Aurora"), "operator");

        Assert.Equal("aurora", package.Slug);
        Assert.Equal(1, package.ActiveRevisionNumber);
        Assert.NotNull(job);
        Assert.Equal(package.Revisions.Single().Id, job.PackageRevisionId);
        Assert.Equal("operator", package.Revisions.Single().CreatedBy);
        Assert.Equal(SourceStatus.Pending, job.Status);
        Assert.Equal(1, await db.PackageDefinitions.CountAsync());
        Assert.Equal(1, await db.PackageRevisions.CountAsync());
        Assert.Equal(1, await db.SourceJobs.CountAsync());
    }

    [Fact]
    public async Task Update_AppendsRevisionAndPreservesPreviousFetch()
    {
        await using var db = CreateDb();
        var catalog = CreateCatalog(db);
        await catalog.CreateAsync(GitRequest("aurora"), "operator");

        var (package, job) = await catalog.UpdateAsync(
            "aurora",
            GitRequest("aurora") with
            {
                SourceReference = "v2.0.0",
                ExpectedRevision = 1
            },
            "operator");

        Assert.Equal(2, package.ActiveRevisionNumber);
        Assert.Equal(2, await db.PackageRevisions.CountAsync());
        Assert.Equal(2, await db.SourceJobs.CountAsync());
        Assert.Equal("v2.0.0", job!.SourceBranch);
        Assert.Equal(2, job.PackageRevision!.RevisionNumber);
    }

    [Fact]
    public async Task Update_RejectsStaleExpectedRevision()
    {
        await using var db = CreateDb();
        var catalog = CreateCatalog(db);
        await catalog.CreateAsync(GitRequest("aurora"), "operator");

        await Assert.ThrowsAsync<PackageConflictException>(() =>
            catalog.UpdateAsync(
                "aurora",
                GitRequest("aurora") with { ExpectedRevision = 0 },
                "operator"));
    }

    [Fact]
    public async Task Create_CanSaveWithoutQueuing()
    {
        await using var db = CreateDb();
        var catalog = CreateCatalog(db);

        var (_, job) = await catalog.CreateAsync(
            GitRequest("aurora") with { FetchAutomatically = false },
            "operator");

        Assert.Null(job);
        Assert.Empty(await db.SourceJobs.ToListAsync());
    }

    [Fact]
    public async Task FetchAll_QueuesOnlyEnabledActiveRevisions()
    {
        await using var db = CreateDb();
        var queue = CreateQueue(db);
        var catalog = new PackageCatalogService(db, queue);
        await catalog.CreateAsync(
            GitRequest("active") with { FetchAutomatically = false },
            "operator");
        db.ChangeTracker.Clear();
        queue = CreateQueue(db);
        catalog = new PackageCatalogService(db, queue);
        await catalog.UpdateAsync(
            "active",
            GitRequest("active") with
            {
                SourceReference = "v2",
                FetchAutomatically = false,
                ExpectedRevision = 1
            },
            "operator");
        db.ChangeTracker.Clear();
        queue = CreateQueue(db);
        catalog = new PackageCatalogService(db, queue);
        await catalog.CreateAsync(
            GitRequest("disabled") with
            {
                IsEnabled = false,
                FetchAutomatically = false
            },
            "operator");
        db.ChangeTracker.Clear();
        queue = CreateQueue(db);

        var jobs = await queue.EnqueueAllAsync();

        var job = Assert.Single(jobs);
        Assert.Equal("active", job.PackageName);
        Assert.Equal("v2", job.SourceBranch);
        Assert.Equal(2, job.PackageRevision!.RevisionNumber);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("white space")]
    [InlineData("")]
    public void InvalidSlugsAreRejected(string slug)
    {
        Assert.Throws<SourceValidationException>(() =>
            PackageSourcePolicy.Validate(GitRequest(slug)));
    }

    [Fact]
    public void ArchiveRequiresExpectedDigest()
    {
        var request = new SavePackageSourceRequest(
            "archive",
            "https://example.test/archive.tar.gz",
            SourceType.Tar);

        var exception = Assert.Throws<SourceValidationException>(() =>
            PackageSourcePolicy.Validate(request));

        Assert.Contains("SHA-256", exception.Message);
    }

    [Theory]
    [InlineData("http://example.test/project.git")]
    [InlineData("https://user:secret@example.test/project.git")]
    [InlineData("not-a-url")]
    public void UnsafeSourceUrlsAreRejected(string sourceUrl)
    {
        Assert.Throws<SourceValidationException>(() =>
            PackageSourcePolicy.Validate(
                GitRequest("package") with { SourceUrl = sourceUrl }));
    }

    [Theory]
    [InlineData("-option")]
    [InlineData("../escape")]
    [InlineData("bad ref")]
    public void UnsafeGitReferencesAreRejected(string reference)
    {
        Assert.Throws<SourceValidationException>(() =>
            PackageSourcePolicy.Validate(
                GitRequest("package") with { SourceReference = reference }));
    }

    private static SavePackageSourceRequest GitRequest(string slug) => new(
        slug,
        "https://example.test/project.git",
        SourceType.Git,
        "main");

    private static SourceDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<SourceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new SourceDbContext(options);
    }

    private static PackageCatalogService CreateCatalog(SourceDbContext db)
        => new(db, CreateQueue(db));

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
