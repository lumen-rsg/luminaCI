using System.Text.Json;
using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.Events;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumina.BuildService.Tests;

public class PipelineRunCoordinatorTests
{
    [Fact]
    public async Task CompleteBuild_does_not_run_undeclared_steps()
    {
        await using var provider = BuildProvider(
            nameof(CompleteBuild_does_not_run_undeclared_steps),
            Guid.NewGuid());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var db = provider.GetRequiredService<BuildDbContext>();
        var job = CreateJob(Step(StepType.Build, 1, StepStatus.Running));
        db.BuildJobs.Add(job);
        await db.SaveChangesAsync();

        var coordinator = provider.GetRequiredService<PipelineRunCoordinator>();
        await coordinator.CompleteBuildStepAsync(job.Id);

        Assert.Equal(StepStatus.Success, job.StepRuns[0].Status);
        Assert.Equal(BuildStatus.Success, job.Status);
        Assert.True(await harness.Published.Any<HashStoreRequested>());
        Assert.False(await harness.Published.Any<CveScanRequested>());
        Assert.False(await harness.Published.Any<PackageSigningRequested>());
        Assert.False(await harness.Published.Any<PackagePublishRequested>());
    }

    [Fact]
    public async Task CompleteBuild_advances_declared_steps_in_order()
    {
        var keyId = Guid.NewGuid();
        await using var provider = BuildProvider(
            nameof(CompleteBuild_advances_declared_steps_in_order),
            keyId);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var db = provider.GetRequiredService<BuildDbContext>();
        var repositoryId = Guid.NewGuid();
        var job = CreateJob(
            Step(StepType.Build, 1, StepStatus.Running),
            Step(StepType.Scan, 2),
            Step(StepType.Sign, 3),
            Step(StepType.Publish, 4, configuration: new() { ["repositoryId"] = repositoryId.ToString() }));
        db.BuildJobs.Add(job);
        await db.SaveChangesAsync();

        var coordinator = provider.GetRequiredService<PipelineRunCoordinator>();
        await coordinator.CompleteBuildStepAsync(job.Id);

        Assert.Equal(StepStatus.Success, job.StepRuns[0].Status);
        Assert.Equal(StepStatus.Running, job.StepRuns[1].Status);
        Assert.True(await harness.Published.Any<HashStoreRequested>());
        Assert.True(await harness.Published.Any<CveScanRequested>());
        Assert.False(await harness.Published.Any<PackageSigningRequested>());
        Assert.False(await harness.Published.Any<PackagePublishRequested>());

        await coordinator.ReportScanAsync(new CveScanCompleted(
            job.Artifacts[0].Id,
            job.Artifacts[0].HashSha256!,
            ScanStatus.Completed,
            0, 0, 0, 0, 0,
            DateTime.UtcNow));

        Assert.Equal(StepStatus.Success, job.StepRuns[1].Status);
        Assert.Equal(StepStatus.Running, job.StepRuns[2].Status);
        Assert.True(await harness.Published.Any<PackageSigningRequested>());
        Assert.False(await harness.Published.Any<PackagePublishRequested>());

        job.Artifacts[0].SigningKeyFingerprint = "ABC123";
        job.Artifacts[0].SignedAt = DateTime.UtcNow;
        job.Artifacts[0].StoragePath = $"sha256/{job.Artifacts[0].HashSha256}/pkg.rpm";
        await db.SaveChangesAsync();
        await coordinator.ReportSignedAsync(job.Artifacts[0].Id);

        Assert.Equal(StepStatus.Success, job.StepRuns[2].Status);
        Assert.Equal(StepStatus.Running, job.StepRuns[3].Status);
        Assert.True(await harness.Published.Any<PackagePublishRequested>());
        var publication = await harness.Published.SelectAsync<PackagePublishRequested>().First();
        Assert.Equal(PromotionSetIdentity.Create(job.Id, $"build-{job.Id:N}"), publication.Context.Message.PromotionSetId);
        Assert.Equal($"build-{job.Id:N}", publication.Context.Message.PromotionGroup);
        Assert.Equal(job.TargetArchitecture, publication.Context.Message.TargetArchitecture);

        await coordinator.ReportPublishedAsync(new PackagePublished(
            job.Artifacts[0].Id,
            repositoryId,
            Guid.NewGuid(),
            DateTime.UtcNow));

        Assert.Equal(StepStatus.Success, job.StepRuns[3].Status);
        Assert.Equal(BuildStatus.Success, job.Status);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public async Task Failed_scan_stops_pipeline_and_skips_later_steps()
    {
        await using var provider = BuildProvider(
            nameof(Failed_scan_stops_pipeline_and_skips_later_steps),
            Guid.NewGuid());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var db = provider.GetRequiredService<BuildDbContext>();
        var job = CreateJob(
            Step(StepType.Build, 1, StepStatus.Success),
            Step(StepType.Scan, 2, StepStatus.Running),
            Step(StepType.Sign, 3));
        db.BuildJobs.Add(job);
        await db.SaveChangesAsync();

        var coordinator = provider.GetRequiredService<PipelineRunCoordinator>();
        await coordinator.ReportScanAsync(new CveScanCompleted(
            job.Artifacts[0].Id,
            job.Artifacts[0].HashSha256!,
            ScanStatus.Completed,
            0, 1, 0, 0, 0,
            DateTime.UtcNow));

        Assert.Equal(BuildStatus.Failed, job.Status);
        Assert.Equal(StepStatus.Failed, job.StepRuns[1].Status);
        Assert.Equal(StepStatus.Skipped, job.StepRuns[2].Status);
        Assert.False(await harness.Published.Any<PackageSigningRequested>());
    }

    [Fact]
    public async Task Scan_result_with_different_digest_stops_pipeline()
    {
        await using var provider = BuildProvider(
            nameof(Scan_result_with_different_digest_stops_pipeline),
            Guid.NewGuid());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var db = provider.GetRequiredService<BuildDbContext>();
        var job = CreateJob(
            Step(StepType.Build, 1, StepStatus.Success),
            Step(StepType.Scan, 2, StepStatus.Running),
            Step(StepType.Sign, 3));
        db.BuildJobs.Add(job);
        await db.SaveChangesAsync();

        var coordinator = provider.GetRequiredService<PipelineRunCoordinator>();
        await coordinator.ReportScanAsync(new CveScanCompleted(
            job.Artifacts[0].Id,
            new string('f', 64),
            ScanStatus.Completed,
            0, 0, 0, 0, 0,
            DateTime.UtcNow));

        Assert.Equal(BuildStatus.Failed, job.Status);
        Assert.Equal(StepStatus.Failed, job.StepRuns[1].Status);
        Assert.Contains("immutable SHA-256", job.StepRuns[1].Error);
        Assert.False(await harness.Published.Any<PackageSigningRequested>());
    }

    [Fact]
    public async Task Candidate_acknowledgement_is_durable_without_completing_publish()
    {
        await using var provider = BuildProvider(
            nameof(Candidate_acknowledgement_is_durable_without_completing_publish),
            Guid.NewGuid());
        var db = provider.GetRequiredService<BuildDbContext>();
        var repositoryId = Guid.NewGuid();
        var job = CreateJob(
            Step(StepType.Build, 1, StepStatus.Success),
            Step(StepType.Publish, 2, StepStatus.Running,
                new() { ["repositoryId"] = repositoryId.ToString() }));
        db.BuildJobs.Add(job);
        await db.SaveChangesAsync();
        var group = $"build-{job.Id:N}";
        var setId = PromotionSetIdentity.Create(job.Id, group);
        var stagedAt = DateTime.UtcNow;
        var result = new PackageCandidateStaged(
            job.Artifacts[0].Id, repositoryId, Guid.NewGuid(), setId, stagedAt);

        var coordinator = provider.GetRequiredService<PipelineRunCoordinator>();
        await coordinator.ReportCandidateStagedAsync(result);
        await coordinator.ReportCandidateStagedAsync(result);

        var artifact = job.Artifacts[0];
        Assert.Equal(repositoryId, artifact.CandidateRepositoryId);
        Assert.Equal(result.CandidatePackageId, artifact.CandidatePackageId);
        Assert.Equal(setId, artifact.PromotionSetId);
        Assert.Equal(stagedAt, artifact.CandidateStagedAt);
        Assert.Equal(StepStatus.Running, job.StepRuns[1].Status);
        Assert.Equal(BuildStatus.Building, job.Status);
    }

    [Fact]
    public async Task Candidate_acknowledgement_with_changed_identity_fails_publish()
    {
        await using var provider = BuildProvider(
            nameof(Candidate_acknowledgement_with_changed_identity_fails_publish),
            Guid.NewGuid());
        var db = provider.GetRequiredService<BuildDbContext>();
        var repositoryId = Guid.NewGuid();
        var job = CreateJob(
            Step(StepType.Build, 1, StepStatus.Success),
            Step(StepType.Publish, 2, StepStatus.Running,
                new() { ["repositoryId"] = repositoryId.ToString() }));
        db.BuildJobs.Add(job);
        await db.SaveChangesAsync();

        var coordinator = provider.GetRequiredService<PipelineRunCoordinator>();
        await coordinator.ReportCandidateStagedAsync(new PackageCandidateStaged(
            job.Artifacts[0].Id,
            repositoryId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTime.UtcNow));

        Assert.Equal(StepStatus.Failed, job.StepRuns[1].Status);
        Assert.Equal(BuildStatus.Failed, job.Status);
        Assert.Contains("immutable publication request", job.StepRuns[1].Error);
    }

    private static ServiceProvider BuildProvider(string databaseName, Guid keyId)
    {
        var options = new DbContextOptionsBuilder<BuildDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        var services = new ServiceCollection()
            .AddSingleton<BuildDbContext>(new TestBuildDbContext(options))
            .AddSingleton<PipelineRunCoordinator>()
            .AddSingleton<ILogger<PipelineRunCoordinator>>(NullLogger<PipelineRunCoordinator>.Instance);

        services.AddMassTransitTestHarness(configurator =>
        {
            configurator.AddHandler<GetActiveSigningKey, ActiveSigningKey>(
                (ConsumeContext<GetActiveSigningKey> _) =>
                    Task.FromResult(new ActiveSigningKey(keyId)));
        });

        return services.BuildServiceProvider();
    }

    private static BuildJob CreateJob(params BuildStepRun[] steps)
    {
        var jobId = Guid.NewGuid();
        return new BuildJob
        {
            Id = jobId,
            PipelineId = Guid.NewGuid(),
            SpecName = "pkg.spec",
            TriggeredBy = "tests",
            Status = BuildStatus.Building,
            StepRuns = steps.ToList(),
            Artifacts =
            [
                new BuildArtifact
                {
                    Id = Guid.NewGuid(),
                    BuildJobId = jobId,
                    FileName = "pkg.rpm",
                    FilePath = "/tmp/pkg.rpm",
                    FileSize = 10,
                    HashSha256 = new string('a', 64),
                    HashMd5 = new string('b', 32)
                }
            ]
        };
    }

    private static BuildStepRun Step(
        StepType type,
        int order,
        StepStatus status = StepStatus.Pending,
        Dictionary<string, string>? configuration = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            PipelineStepId = Guid.NewGuid(),
            Type = type,
            Name = type.ToString(),
            Order = order,
            Status = status,
            StartedAt = status == StepStatus.Running ? DateTime.UtcNow : null,
            CompletedAt = status == StepStatus.Success ? DateTime.UtcNow : null,
            Configuration = configuration ?? new()
        };

    private sealed class TestBuildDbContext(DbContextOptions<BuildDbContext> options)
        : BuildDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Pipeline>().Property(p => p.Tags).HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<List<string>>(value, (JsonSerializerOptions?)null) ?? new());
            modelBuilder.Entity<PipelineStep>().Property(p => p.Configuration).HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<Dictionary<string, string>>(value, (JsonSerializerOptions?)null) ?? new());
            modelBuilder.Entity<BuildStepRun>().Property(p => p.Configuration).HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<Dictionary<string, string>>(value, (JsonSerializerOptions?)null) ?? new());
        }
    }
}
