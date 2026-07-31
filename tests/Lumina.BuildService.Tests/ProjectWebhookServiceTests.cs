using System.Text.Json;
using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.Events;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class ProjectWebhookServiceTests
{
    [Fact]
    public void ParseGitHubPush_ExtractsExactCommitAndChangedPaths()
    {
        using var document = JsonDocument.Parse("""
            {
              "after": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "ref": "refs/heads/main",
              "commits": [{"added":["one/new"],"modified":["two/file"],"removed":[]}],
              "head_commit": {"message":"Build packages\nDetails","author":{"username":"cv2"}}
            }
            """);

        var push = ProjectWebhookService.ParseGitHubPush(document.RootElement);

        Assert.Equal(new string('a', 40), push.CommitSha);
        Assert.Equal("main", push.Branch);
        Assert.Equal(["one/new", "two/file"], push.ChangedPaths.Order().ToArray());
        Assert.Equal("cv2", push.Author);
        Assert.Equal("Build packages", push.Message);
    }

    [Fact]
    public async Task QueueSnapshotAsync_PersistsAndPublishesIdempotently()
    {
        await using var db = CreateDb(nameof(QueueSnapshotAsync_PersistsAndPublishesIdempotently));
        var publisher = new RecordingPublisher();
        var service = new ProjectWebhookService(db, publisher);
        var project = AddProject(db);
        await db.SaveChangesAsync();
        var push = Push();

        var first = await service.QueueSnapshotAsync(project, "delivery-1", push, default);
        var second = await service.QueueSnapshotAsync(project, "delivery-1", push, default);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(["a/file", "z/file"], first.ChangedPaths);
        Assert.Equal(first.Id, Assert.Single(publisher.Requests).RequestId);
        Assert.Equal(1, await db.ProjectWebhookDeliveries.CountAsync());
    }

    [Fact]
    public async Task QueueSnapshotAsync_RejectsWrongBranchAndCredentialedProject()
    {
        await using var db = CreateDb(nameof(QueueSnapshotAsync_RejectsWrongBranchAndCredentialedProject));
        var service = new ProjectWebhookService(db, new RecordingPublisher());
        var project = AddProject(db);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.QueueSnapshotAsync(project, "delivery-1", Push() with { Branch = "other" }, default));
        project.GitUsername = "git";
        project.GitToken = "secret";
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.QueueSnapshotAsync(project, "delivery-2", Push(), default));
    }

    private static GitHubPush Push() => new(
        new string('a', 40),
        "main",
        new HashSet<string>(StringComparer.Ordinal) { "z/file", "a/file" },
        "cv2",
        "message");

    private static BuildProject AddProject(BuildDbContext db)
    {
        var project = new BuildProject
        {
            Id = Guid.NewGuid(),
            Name = "Lumina",
            GitRepoUrl = "https://github.com/lumina/packages.git",
            GitBranch = "main",
            ManifestPath = ".lumina/packages.yaml",
            WebhookSecret = new string('s', 32),
            CreatedBy = "cv2"
        };
        db.BuildProjects.Add(project);
        return project;
    }

    private static BuildDbContext CreateDb(string name)
    {
        var options = new DbContextOptionsBuilder<BuildDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new TestBuildDbContext(options);
    }

    private sealed class RecordingPublisher : IRepositorySnapshotPublisher
    {
        public List<RepositorySnapshotRequested> Requests { get; } = [];
        public Task PublishAsync(RepositorySnapshotRequested request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class TestBuildDbContext(DbContextOptions<BuildDbContext> options)
        : BuildDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<PipelineStep>().Ignore(step => step.Configuration);
            modelBuilder.Entity<BuildStepRun>().Ignore(step => step.Configuration);
        }
    }
}
