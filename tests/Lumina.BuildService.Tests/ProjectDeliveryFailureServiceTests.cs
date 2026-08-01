using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.BuildService.Services.PackageGraph;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class ProjectDeliveryFailureServiceTests
{
    [Fact]
    public async Task FailAsync_ReconcilesHistoricalFailedDeliveryWithOpenPublishStep()
    {
        await using var db = CreateDb(
            nameof(FailAsync_ReconcilesHistoricalFailedDeliveryWithOpenPublishStep));
        var (delivery, job) = AddDelivery(db, ProjectWebhookStatus.Failed, StepType.Publish);
        delivery.FailureCode = "candidate-staging-failed";
        await db.SaveChangesAsync();
        var executor = new RecordingExecutor(BuildExecutorBackend.Kubernetes);

        var changed = await NewService(db, executor).FailAsync(
            delivery.Id,
            "project-delivery-failed",
            default);

        Assert.True(changed);
        Assert.Equal(ProjectWebhookStatus.Failed, delivery.Status);
        Assert.Equal("candidate-staging-failed", delivery.FailureCode);
        Assert.Equal(BuildStatus.Cancelled, job.Status);
        Assert.Null(job.LeaseOwner);
        Assert.Equal(StepStatus.Skipped, Assert.Single(job.StepRuns).Status);
        Assert.Empty(executor.CancelledBuilds);
    }

    [Fact]
    public async Task FailAsync_CancelsExecutorWhenBuildStepIsStillRunning()
    {
        await using var db = CreateDb(nameof(FailAsync_CancelsExecutorWhenBuildStepIsStillRunning));
        var (delivery, job) = AddDelivery(db, ProjectWebhookStatus.Dispatched, StepType.Build);
        await db.SaveChangesAsync();
        var executor = new RecordingExecutor(BuildExecutorBackend.Kubernetes);

        await NewService(db, executor).FailAsync(
            delivery.Id,
            "project-build-failed",
            default);

        Assert.Equal([job.Id], executor.CancelledBuilds);
        Assert.Equal(ProjectWebhookStatus.Failed, delivery.Status);
        Assert.Equal(BuildStatus.Cancelled, job.Status);
        Assert.Equal(StepStatus.Skipped, Assert.Single(job.StepRuns).Status);
    }

    private static ProjectDeliveryFailureService NewService(
        BuildDbContext db,
        IBuildExecutor executor) => new(
            db,
            new RecordingResolver(executor),
            NullLogger<ProjectDeliveryFailureService>.Instance);

    private static (ProjectWebhookDelivery Delivery, BuildJob Job) AddDelivery(
        BuildDbContext db,
        ProjectWebhookStatus deliveryStatus,
        StepType runningStep)
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
        var delivery = new ProjectWebhookDelivery
        {
            Id = Guid.NewGuid(),
            BuildProjectId = project.Id,
            BuildProject = project,
            ProviderDeliveryId = Guid.NewGuid().ToString("N"),
            RepositoryUrl = project.GitRepoUrl,
            CommitSha = new string('a', 40),
            Branch = "main",
            Status = deliveryStatus
        };
        var job = new BuildJob
        {
            Id = Guid.NewGuid(),
            PipelineId = Guid.NewGuid(),
            ProjectWebhookDeliveryId = delivery.Id,
            ProjectWebhookDelivery = delivery,
            ProjectPackageId = "firmware",
            ProjectStageOrder = 0,
            Status = BuildStatus.Building,
            ExecutionBackend = BuildExecutorBackend.Kubernetes,
            LeaseOwner = "worker",
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(1),
            StepRuns =
            [
                new BuildStepRun
                {
                    Id = Guid.NewGuid(),
                    PipelineStepId = Guid.NewGuid(),
                    Type = runningStep,
                    Name = runningStep.ToString(),
                    Status = StepStatus.Running
                }
            ]
        };
        delivery.BuildJobs.Add(job);
        db.ProjectWebhookDeliveries.Add(delivery);
        return (delivery, job);
    }

    private static BuildDbContext CreateDb(string name)
    {
        var options = new DbContextOptionsBuilder<BuildDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new TestBuildDbContext(options);
    }

    private sealed class RecordingResolver(IBuildExecutor executor) : IBuildExecutorResolver
    {
        public IBuildExecutor Resolve(BuildExecutorBackend backend)
        {
            Assert.Equal(executor.Backend, backend);
            return executor;
        }
    }

    private sealed class RecordingExecutor(BuildExecutorBackend backend) : IBuildExecutor
    {
        public BuildExecutorBackend Backend { get; } = backend;
        public List<Guid> CancelledBuilds { get; } = [];

        public Task<bool> CancelBuildAsync(
            Guid jobId,
            CancellationToken cancellationToken = default)
        {
            CancelledBuilds.Add(jobId);
            return Task.FromResult(true);
        }

        public Task<BuildJob> StartBuildAsync(
            BuildJob job,
            string? specContent,
            string? sourceUrl,
            string? buildImage = null,
            string? gitUsername = null,
            string? gitToken = null,
            string? extraSourcesPipelineDir = null) => throw new NotSupportedException();

        public Task MonitorBuildAsync(
            BuildJob job,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
