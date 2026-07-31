using System.Text.Json;
using Lumina.BuildService.Data;
using Lumina.BuildService.Services.PackageGraph;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class ProjectDispatchServiceTests
{
    [Fact]
    public async Task AdvanceAsync_LaunchesAndCompletesDependencyStagesInOrder()
    {
        await using var db = CreateDb(nameof(AdvanceAsync_LaunchesAndCompletesDependencyStagesInOrder));
        var delivery = AddDelivery(db);
        await db.SaveChangesAsync();
        var trigger = new RecordingBuildTrigger(db);
        var dispatcher = NewDispatcher(db, trigger);

        await dispatcher.AdvanceAsync(delivery.Id, default);

        Assert.Equal(ProjectWebhookStatus.Dispatched, delivery.Status);
        Assert.Equal(["firmware"], trigger.Launches.Select(item => item.PackageId).ToArray());
        var firmware = Assert.Single(delivery.BuildJobs);
        Assert.Equal(0, firmware.ProjectStageOrder);
        Assert.Equal(delivery.CommitSha, Assert.Single(trigger.Launches).CommitSha);
        await dispatcher.AdvanceAsync(delivery.Id, default);
        Assert.Single(trigger.Launches);

        firmware.Status = BuildStatus.Success;
        await db.SaveChangesAsync();
        await dispatcher.AdvanceAsync(delivery.Id, default);

        Assert.Equal(["firmware", "driver"], trigger.Launches.Select(item => item.PackageId).ToArray());
        var driver = delivery.BuildJobs.Single(job => job.ProjectPackageId == "driver");
        Assert.Equal(1, driver.ProjectStageOrder);
        Assert.Equal(ProjectWebhookStatus.Dispatched, delivery.Status);

        driver.Status = BuildStatus.Success;
        await db.SaveChangesAsync();
        await dispatcher.AdvanceAsync(delivery.Id, default);

        Assert.Equal(ProjectWebhookStatus.Completed, delivery.Status);
        Assert.Null(delivery.FailureCode);
        Assert.Equal(2, trigger.Launches.Count);
    }

    [Fact]
    public async Task AdvanceAsync_FailsDeliveryWhenCurrentStageFails()
    {
        await using var db = CreateDb(nameof(AdvanceAsync_FailsDeliveryWhenCurrentStageFails));
        var delivery = AddDelivery(db);
        await db.SaveChangesAsync();
        var dispatcher = NewDispatcher(db, new RecordingBuildTrigger(db));
        await dispatcher.AdvanceAsync(delivery.Id, default);
        delivery.BuildJobs.Single().Status = BuildStatus.Failed;
        await db.SaveChangesAsync();

        await dispatcher.AdvanceAsync(delivery.Id, default);

        Assert.Equal(ProjectWebhookStatus.Failed, delivery.Status);
        Assert.Equal("project-build-failed", delivery.FailureCode);
        Assert.Single(delivery.BuildJobs);
    }

    [Fact]
    public async Task AdvanceAsync_RecoversPartiallyCreatedParallelStageIdempotently()
    {
        await using var db = CreateDb(nameof(AdvanceAsync_RecoversPartiallyCreatedParallelStageIdempotently));
        var delivery = AddDelivery(db, parallel: true);
        var existing = Job(delivery, "firmware", 0, delivery.BuildProject.Pipelines[0].Id);
        existing.Status = BuildStatus.Success;
        db.BuildJobs.Add(existing);
        await db.SaveChangesAsync();
        var trigger = new RecordingBuildTrigger(db);

        await NewDispatcher(db, trigger).AdvanceAsync(delivery.Id, default);

        Assert.Equal(["driver"], trigger.Launches.Select(item => item.PackageId).ToArray());
        Assert.Equal(ProjectWebhookStatus.Dispatched, delivery.Status);
        Assert.Equal(2, delivery.BuildJobs.Count);
    }

    [Fact]
    public async Task AdvanceAsync_ContinuesStagesAfterCandidatesAndWaitsForPromotion()
    {
        await using var db = CreateDb(
            nameof(AdvanceAsync_ContinuesStagesAfterCandidatesAndWaitsForPromotion));
        var delivery = AddDelivery(db);
        await db.SaveChangesAsync();
        var trigger = new RecordingBuildTrigger(db);
        var dispatcher = NewDispatcher(db, trigger);

        await dispatcher.AdvanceAsync(delivery.Id, default);
        var firmware = Assert.Single(delivery.BuildJobs);
        StageCandidate(db, firmware, delivery.Id, "jetson-r39.2");
        await db.SaveChangesAsync();

        await dispatcher.AdvanceAsync(delivery.Id, default);

        Assert.Equal(["firmware", "driver"],
            trigger.Launches.Select(item => item.PackageId).ToArray());
        var driver = delivery.BuildJobs.Single(job => job.ProjectPackageId == "driver");
        StageCandidate(db, driver, delivery.Id, "jetson-r39.2");
        await db.SaveChangesAsync();

        await dispatcher.AdvanceAsync(delivery.Id, default);

        Assert.Equal(ProjectWebhookStatus.PromotionPending, delivery.Status);
        Assert.All(delivery.BuildJobs, job => Assert.Equal(BuildStatus.Building, job.Status));
        Assert.All(delivery.BuildJobs, job =>
            Assert.Equal(StepStatus.Running, job.StepRuns.Single().Status));
    }

    [Fact]
    public async Task AdvanceAsync_FailsClosedWhenBindingChangedAfterPlanning()
    {
        await using var db = CreateDb(nameof(AdvanceAsync_FailsClosedWhenBindingChangedAfterPlanning));
        var delivery = AddDelivery(db);
        delivery.BuildProject.Pipelines.Single(item => item.PackageId == "firmware").BuildProfile = "fedora-44-x86_64";
        await db.SaveChangesAsync();
        var trigger = new RecordingBuildTrigger(db);

        await NewDispatcher(db, trigger).AdvanceAsync(delivery.Id, default);

        Assert.Equal(ProjectWebhookStatus.Failed, delivery.Status);
        Assert.Equal("dispatch-invalid", delivery.FailureCode);
        Assert.Empty(trigger.Launches);
    }

    [Fact]
    public async Task AdvanceAsync_FailsClosedForCorruptedPersistedPlan()
    {
        await using var db = CreateDb(nameof(AdvanceAsync_FailsClosedForCorruptedPersistedPlan));
        var delivery = AddDelivery(db);
        delivery.DispatchPlanJson = """
            {"stages":[{"order":0,"targets":[null]}],"selected":[],"skipped":[],"isConservative":false}
            """;
        await db.SaveChangesAsync();

        await NewDispatcher(db, new RecordingBuildTrigger(db)).AdvanceAsync(delivery.Id, default);

        Assert.Equal(ProjectWebhookStatus.Failed, delivery.Status);
        Assert.Equal("dispatch-invalid", delivery.FailureCode);
    }

    [Fact]
    public void BuildLaunchValidation_RejectsDescriptorDelimiterInjection()
    {
        var launch = new ProjectBuildLaunch(
            Guid.NewGuid(), Guid.NewGuid(), "driver", 0,
            "https://github.com/lumina/packages.git", "main", new string('a', 40),
            "specs/driver.spec", null, null);

        ProjectBuildTrigger.Validate(launch);
        var request = ProjectBuildTrigger.CreateRequest(launch);
        Assert.Contains($"commit={launch.CommitSha}", request.SourceUrl, StringComparison.Ordinal);
        Assert.Equal($"project:{launch.DeliveryId:N}:driver", request.IdempotencyKey);
        Assert.Equal(launch.CommitSha, request.CommitSha);
        Assert.Throws<ValidationException>(() =>
            ProjectBuildTrigger.Validate(launch with { SpecPath = "specs/driver.spec&commit=bad" }));
        Assert.Throws<ValidationException>(() =>
            ProjectBuildTrigger.Validate(launch with { RepositoryUrl = "https://user:token@example.com/repo.git" }));
    }

    private static ProjectDispatchService NewDispatcher(
        BuildDbContext db,
        IProjectBuildTrigger trigger) =>
        new(db, trigger, NullLogger<ProjectDispatchService>.Instance);

    private static ProjectWebhookDelivery AddDelivery(BuildDbContext db, bool parallel = false)
    {
        var projectId = Guid.NewGuid();
        var firmware = Pipeline(projectId, "firmware", "specs/firmware.spec");
        var driver = Pipeline(projectId, "driver", "specs/driver.spec");
        var project = new BuildProject
        {
            Id = projectId,
            Name = "Lumina",
            GitRepoUrl = "https://github.com/lumina/packages.git",
            GitBranch = "main",
            ManifestPath = ".lumina/packages.yaml",
            WebhookSecret = new string('s', 32),
            CreatedBy = "cv2",
            Pipelines = [firmware, driver]
        };
        var stages = parallel
            ? new List<ProjectDispatchStage>
            {
                new(0,
                [
                    new("firmware", firmware.Id, firmware.BuildProfile, firmware.SpecPath!),
                    new("driver", driver.Id, driver.BuildProfile, driver.SpecPath!)
                ])
            }
            : new List<ProjectDispatchStage>
            {
                new(0, [new("firmware", firmware.Id, firmware.BuildProfile, firmware.SpecPath!)]),
                new(1, [new("driver", driver.Id, driver.BuildProfile, driver.SpecPath!)])
            };
        var delivery = new ProjectWebhookDelivery
        {
            Id = Guid.NewGuid(),
            BuildProjectId = projectId,
            BuildProject = project,
            ProviderDeliveryId = Guid.NewGuid().ToString("N"),
            RepositoryUrl = project.GitRepoUrl,
            CommitSha = new string('a', 40),
            Branch = "main",
            ChangedPaths = ["firmware/blob"],
            Status = ProjectWebhookStatus.PlanReady,
            ManifestSha256 = new string('b', 64),
            DispatchPlanJson = JsonSerializer.Serialize(
                new ProjectDispatchPlan(stages, [], [], false),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        db.BuildProjects.Add(project);
        db.ProjectWebhookDeliveries.Add(delivery);
        return delivery;
    }

    private static Pipeline Pipeline(Guid projectId, string packageId, string specPath) => new()
    {
        Id = Guid.NewGuid(),
        BuildProjectId = projectId,
        PackageId = packageId,
        Name = packageId,
        Description = packageId,
        Status = PipelineStatus.Active,
        CreatedBy = "cv2",
        SpecPath = specPath,
        BuildProfile = "fedora-44-aarch64"
    };

    private static BuildJob Job(
        ProjectWebhookDelivery delivery,
        string packageId,
        int stageOrder,
        Guid pipelineId) => new()
    {
        Id = Guid.NewGuid(),
        PipelineId = pipelineId,
        ProjectWebhookDeliveryId = delivery.Id,
        ProjectWebhookDelivery = delivery,
        ProjectPackageId = packageId,
        ProjectStageOrder = stageOrder,
        SpecName = $"{packageId}.spec",
        TriggeredBy = $"project:{delivery.Id:N}",
        TargetDistribution = "fedora",
        TargetRelease = "44",
        TargetArchitecture = "aarch64",
        BuildProfile = "fedora-44-aarch64",
        StepRuns =
        [
            new BuildStepRun
            {
                Id = Guid.NewGuid(),
                BuildJobId = Guid.Empty,
                PipelineStepId = Guid.NewGuid(),
                Type = StepType.Publish,
                Name = "Publish",
                Order = 1,
                Status = StepStatus.Running
            }
        ]
    };

    private static void StageCandidate(
        BuildDbContext db,
        BuildJob job,
        Guid deliveryId,
        string promotionGroup)
    {
        job.Status = BuildStatus.Building;
        job.PromotionGroup = promotionGroup;
        var setId = PromotionSetIdentity.Create(deliveryId, promotionGroup);
        var artifact = new BuildArtifact
        {
            Id = Guid.NewGuid(),
            BuildJobId = job.Id,
            FileName = $"{job.ProjectPackageId}.rpm",
            FilePath = "/tmp/package.rpm",
            CandidateRepositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            CandidatePackageId = Guid.NewGuid(),
            PromotionSetId = setId,
            CandidateStagedAt = DateTime.UtcNow
        };
        job.Artifacts.Add(artifact);
        db.BuildArtifacts.Add(artifact);
    }

    private static BuildDbContext CreateDb(string name)
    {
        var options = new DbContextOptionsBuilder<BuildDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new TestBuildDbContext(options);
    }

    private sealed class RecordingBuildTrigger(BuildDbContext db) : IProjectBuildTrigger
    {
        public List<ProjectBuildLaunch> Launches { get; } = [];

        public async Task<BuildJob> TriggerAsync(
            ProjectBuildLaunch launch,
            CancellationToken cancellationToken)
        {
            Launches.Add(launch);
            var existing = await db.BuildJobs.SingleOrDefaultAsync(
                job => job.ProjectWebhookDeliveryId == launch.DeliveryId &&
                       job.ProjectPackageId == launch.PackageId,
                cancellationToken);
            if (existing is not null)
                return existing;
            var delivery = await db.ProjectWebhookDeliveries.SingleAsync(
                item => item.Id == launch.DeliveryId, cancellationToken);
            var job = Job(delivery, launch.PackageId, launch.StageOrder, launch.PipelineId);
            job.Status = BuildStatus.Queued;
            db.BuildJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
            return job;
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
