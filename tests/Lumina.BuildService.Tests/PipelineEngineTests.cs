using System.Collections.Concurrent;
using System.Text.Json;
using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.DTOs;
using Lumina.Shared.Errors;
using Lumina.Shared.Extensions;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumina.BuildService.Tests;

/// <summary>
/// Tests for <see cref="PipelineEngine"/>. The engine sits on the trigger path
/// for both manual and webhook-driven builds, so its fail-closed gates are
/// load-bearing for security: a missing webhook secret or a Sign step without
/// an active key must reject the build BEFORE any container is launched.
///
/// <para>The engine is tested against an in-memory EF Core context and two
/// minimal fakes (<see cref="FakeBuildLauncher"/>,
/// <see cref="RecordingSigningKeyGate"/>). This exercises the engine's own
/// logic — persistence, source-URL synthesis, the Sign-step gate — without
/// standing up Docker or RabbitMQ.</para>
/// </summary>
public class PipelineEngineTests
{
    private static async Task<PipelineEngine> NewEngineAsync(
        IServiceProvider sp,
        ISigningKeyGate? gate = null,
        bool keyActive = true)
    {
        var db = sp.GetRequiredService<BuildDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var launcher = sp.GetRequiredService<FakeBuildLauncher>();
        launcher.Reset();
        var signingGate = gate ?? new RecordingSigningKeyGate(keyActive);

        return new PipelineEngine(
            db,
            launcher,
            NullLogger<PipelineEngine>.Instance,
            sp.GetRequiredService<RedisCacheService>(),
            signingGate);
    }

    /// <summary>
    /// Builds a service provider containing the EF Core InMemory context, the
    /// in-memory distributed cache (so RedisCacheService round-trips work), and
    /// the test fakes. Each test gets a uniquely-named in-memory database, so
    /// the context is registered as a singleton for that provider — simpler
    /// than juggling scopes in tests and fully isolated per provider.
    /// </summary>
    private static ServiceProvider BuildServiceProvider(string dbName)
        => new ServiceCollection()
            .AddSingleton<BuildDbContext>(_ =>
            {
                var options = new DbContextOptionsBuilder<BuildDbContext>()
                    .UseInMemoryDatabase(dbName)
                    .Options;
                return new TestBuildDbContext(options);
            })
            .AddSingleton<FakeBuildLauncher>()
            .AddSingleton<IDistributedCache, InMemoryDistributedCache>()
            .AddSingleton<RedisCacheService>(sp =>
                new RedisCacheService(sp.GetRequiredService<IDistributedCache>(),
                    NullLogger<RedisCacheService>.Instance))
            .BuildServiceProvider();

    private static CreatePipelineRequest BuildRequest(string? webhookSecret, bool withSignStep = false) => new(
        Name: "test-pipeline",
        Description: "d",
        Steps: new List<CreatePipelineStepRequest>
        {
            new(StepType.Build, "build", 1, new Dictionary<string, string>())
        }.Concat(withSignStep
            ? new[]
            {
                new CreatePipelineStepRequest(StepType.Scan, "scan", 2, new Dictionary<string, string>()),
                new CreatePipelineStepRequest(StepType.Sign, "sign", 3, new Dictionary<string, string>())
            }
            : Array.Empty<CreatePipelineStepRequest>()).ToList(),
        Tags: new List<string>(),
        GitRepoUrl: "example.com/repo.git",
        GitBranch: "main",
        WebhookSecret: webhookSecret,
        TargetDistribution: "fedora",
        TargetRelease: "44",
        TargetArchitecture: "aarch64",
        BuildProfile: "fedora-44-aarch64");

    // ─── CreatePipelineAsync: webhook-secret gate ────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreatePipelineAsync_RejectsEmptyWebhookSecret(string? secret)
    {
        // Fail-closed at creation: a pipeline without a secret would have a
        // permanently dead webhook URL, and (worse) was trivially triggerable
        // before the handler-level gate landed. The engine must refuse to
        // create it.
        await using var sp = BuildServiceProvider(nameof(CreatePipelineAsync_RejectsEmptyWebhookSecret));
        var engine = await NewEngineAsync(sp);

        await Assert.ThrowsAsync<ValidationException>(() =>
            engine.CreatePipelineAsync(BuildRequest(secret), "ops"));
    }

    [Fact]
    public async Task CreatePipelineAsync_PersistsPipelineWithSecret()
    {
        await using var sp = BuildServiceProvider(nameof(CreatePipelineAsync_PersistsPipelineWithSecret));
        var engine = await NewEngineAsync(sp);

        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret"), "ops");

        Assert.Equal("test-pipeline", pipeline.Name);
        Assert.Equal(PipelineStatus.Active, pipeline.Status);
        Assert.Equal("s3cret", pipeline.WebhookSecret);
        Assert.Single(pipeline.Steps);
        Assert.Equal(StepType.Build, pipeline.Steps[0].Type);

        // Persisted to the context.
        var fromDb = await sp.GetRequiredService<BuildDbContext>().Pipelines
            .Include(p => p.Steps).FirstAsync(p => p.Id == pipeline.Id);
        Assert.Equal("s3cret", fromDb.WebhookSecret);
    }

    [Fact]
    public async Task CreatePipelineAsync_DefaultsTriggerPathToSpecDirectory()
    {
        await using var sp = BuildServiceProvider(
            nameof(CreatePipelineAsync_DefaultsTriggerPathToSpecDirectory));
        var engine = await NewEngineAsync(sp);
        var request = BuildRequest("s3cret") with
        {
            SpecPath = "common/neofetch/neofetch.spec"
        };

        var pipeline = await engine.CreatePipelineAsync(request, "ops");

        Assert.Equal(["common/neofetch"], pipeline.TriggerPaths);
    }

    [Fact]
    public async Task CreatePipelineAsync_RequiresExplicitBuildTarget()
    {
        await using var sp = BuildServiceProvider(
            nameof(CreatePipelineAsync_RequiresExplicitBuildTarget));
        var engine = await NewEngineAsync(sp);
        var request = BuildRequest("s3cret") with
        {
            TargetDistribution = null,
            TargetRelease = null,
            TargetArchitecture = null,
            BuildProfile = null
        };

        await Assert.ThrowsAsync<ValidationException>(() =>
            engine.CreatePipelineAsync(request, "ops"));
    }

    [Fact]
    public async Task CreatePipelineAsync_RejectsDefinitionWithoutBuildStep()
    {
        await using var sp = BuildServiceProvider(nameof(CreatePipelineAsync_RejectsDefinitionWithoutBuildStep));
        var engine = await NewEngineAsync(sp);
        var request = BuildRequest("s3cret") with
        {
            Steps = [new CreatePipelineStepRequest(StepType.Scan, "scan", 1, new())]
        };

        await Assert.ThrowsAsync<ValidationException>(() =>
            engine.CreatePipelineAsync(request, "ops"));
    }

    [Fact]
    public async Task CreatePipelineAsync_RejectsOutOfOrderDefinition()
    {
        await using var sp = BuildServiceProvider(nameof(CreatePipelineAsync_RejectsOutOfOrderDefinition));
        var engine = await NewEngineAsync(sp);
        var request = BuildRequest("s3cret") with
        {
            Steps =
            [
                new CreatePipelineStepRequest(StepType.Scan, "scan", 1, new()),
                new CreatePipelineStepRequest(StepType.Build, "build", 2, new())
            ]
        };

        await Assert.ThrowsAsync<ValidationException>(() =>
            engine.CreatePipelineAsync(request, "ops"));
    }

    [Fact]
    public async Task CreatePipelineAsync_RejectsPublishWithoutRepository()
    {
        await using var sp = BuildServiceProvider(nameof(CreatePipelineAsync_RejectsPublishWithoutRepository));
        var engine = await NewEngineAsync(sp);
        var request = BuildRequest("s3cret", withSignStep: true) with
        {
            Steps =
            [
                .. BuildRequest("s3cret", withSignStep: true).Steps,
                new CreatePipelineStepRequest(StepType.Publish, "publish", 4, new())
            ]
        };

        await Assert.ThrowsAsync<ValidationException>(() =>
            engine.CreatePipelineAsync(request, "ops"));
    }

    [Fact]
    public async Task UpdatePipelineAsync_RejectsStaleVersion()
    {
        await using var sp = BuildServiceProvider(nameof(UpdatePipelineAsync_RejectsStaleVersion));
        var engine = await NewEngineAsync(sp);
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret"), "ops");
        var staleVersion = pipeline.UpdatedAt.AddSeconds(-1);

        var request = new UpdatePipelineRequest(
            "changed", "d", [], [],
            ExpectedUpdatedAt: staleVersion);

        await Assert.ThrowsAsync<ConflictException>(() =>
            engine.UpdatePipelineAsync(pipeline.Id, request));

        Assert.Equal("test-pipeline", pipeline.Name);
    }

    [Fact]
    public async Task UpdatePipelineAsync_RejectsMissingVersion()
    {
        await using var sp = BuildServiceProvider(nameof(UpdatePipelineAsync_RejectsMissingVersion));
        var engine = await NewEngineAsync(sp);
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret"), "ops");

        var request = new UpdatePipelineRequest("changed", "d", [], []);

        await Assert.ThrowsAsync<ValidationException>(() =>
            engine.UpdatePipelineAsync(pipeline.Id, request));
    }

    [Fact]
    public async Task UpdatePipelineAsync_AcceptsCurrentVersion()
    {
        await using var sp = BuildServiceProvider(nameof(UpdatePipelineAsync_AcceptsCurrentVersion));
        var engine = await NewEngineAsync(sp);
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret"), "ops");

        var request = new UpdatePipelineRequest(
            "changed", "updated",
            [new CreatePipelineStepRequest(StepType.Build, "build", 1, new Dictionary<string, string>())],
            [],
            ExpectedUpdatedAt: pipeline.UpdatedAt,
            TargetDistribution: "fedora",
            TargetRelease: "44",
            TargetArchitecture: "aarch64",
            BuildProfile: "fedora-44-aarch64");

        var updated = await engine.UpdatePipelineAsync(pipeline.Id, request);

        Assert.Equal("changed", updated.Name);
        Assert.Equal("updated", updated.Description);
        Assert.True(updated.UpdatedAt > request.ExpectedUpdatedAt);
    }

    // ─── TriggerBuildAsync: Sign-step key gate ───────────────────────────

    [Fact]
    public async Task TriggerBuildAsync_RejectsSignStep_WhenNoActiveKey()
    {
        // A Sign-enabled pipeline with no active PGP key must be rejected
        // before any build starts — otherwise the artifact is built and scanned
        // only to fail publication later.
        await using var sp = BuildServiceProvider(nameof(TriggerBuildAsync_RejectsSignStep_WhenNoActiveKey));
        var gate = new RecordingSigningKeyGate(keyActive: false);
        var engine = await NewEngineAsync(sp, gate);
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret", withSignStep: true), "ops");

        await Assert.ThrowsAsync<ValidationException>(() =>
            engine.TriggerBuildAsync(pipeline.Id, new TriggerBuildRequest("pkg.spec", "", null, "webhook")));

        // The gate was consulted (not skipped) and no build was launched.
        Assert.True(gate.WasConsulted);
        Assert.False(sp.GetRequiredService<FakeBuildLauncher>().WasLaunched);
    }

    [Fact]
    public async Task TriggerBuildAsync_RejectsSignStep_WhenGateThrows()
    {
        // Fail-closed also covers the "SecurityService unreachable" path: any
        // exception from the gate surfaces as a ValidationException.
        await using var sp = BuildServiceProvider(nameof(TriggerBuildAsync_RejectsSignStep_WhenGateThrows));
        var engine = await NewEngineAsync(sp, gate: new ThrowingSigningKeyGate());
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret", withSignStep: true), "ops");

        await Assert.ThrowsAsync<ValidationException>(() =>
            engine.TriggerBuildAsync(pipeline.Id, new TriggerBuildRequest("pkg.spec", "", null, "webhook")));
    }

    [Fact]
    public async Task TriggerBuildAsync_AllowsSignStep_WhenKeyActive()
    {
        await using var sp = BuildServiceProvider(nameof(TriggerBuildAsync_AllowsSignStep_WhenKeyActive));
        var gate = new RecordingSigningKeyGate(keyActive: true);
        var engine = await NewEngineAsync(sp, gate);
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret", withSignStep: true), "ops");

        var job = await engine.TriggerBuildAsync(pipeline.Id, new TriggerBuildRequest("pkg.spec", "", null, "ops"));

        Assert.True(gate.WasConsulted);
        Assert.Equal(BuildStatus.Queued, job.Status);
    }

    [Fact]
    public async Task TriggerBuildAsync_DoesNotConsultGate_WhenNoSignStep()
    {
        // The gate must only fire for pipelines that declare a Sign step — a
        // build-only pipeline never needs a signing key. Consulting it anyway
        // would couple unrelated builds to SecurityService availability.
        await using var sp = BuildServiceProvider(nameof(TriggerBuildAsync_DoesNotConsultGate_WhenNoSignStep));
        var gate = new RecordingSigningKeyGate(keyActive: false); // would reject if consulted
        var engine = await NewEngineAsync(sp, gate);
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret", withSignStep: false), "ops");

        var job = await engine.TriggerBuildAsync(pipeline.Id, new TriggerBuildRequest("pkg.spec", "", null, "ops"));

        Assert.False(gate.WasConsulted);
        Assert.Equal(BuildStatus.Queued, job.Status);
    }

    [Fact]
    public async Task TriggerBuildAsync_RejectsPausedPipeline()
    {
        await using var sp = BuildServiceProvider(nameof(TriggerBuildAsync_RejectsPausedPipeline));
        var engine = await NewEngineAsync(sp);
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret"), "ops");
        pipeline.Status = PipelineStatus.Paused;
        await sp.GetRequiredService<BuildDbContext>().SaveChangesAsync();

        await Assert.ThrowsAsync<ValidationException>(() =>
            engine.TriggerBuildAsync(
                pipeline.Id,
                new TriggerBuildRequest("pkg.spec", "", null, "ops")));

        Assert.False(sp.GetRequiredService<FakeBuildLauncher>().WasLaunched);
    }

    // ─── TriggerBuildAsync: job persistence + metadata ───────────────────

    [Fact]
    public async Task TriggerBuildAsync_PersistsJobAndLaunchesBuild()
    {
        await using var sp = BuildServiceProvider(nameof(TriggerBuildAsync_PersistsJobAndLaunchesBuild));
        var engine = await NewEngineAsync(sp);
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret"), "ops");

        var job = await engine.TriggerBuildAsync(pipeline.Id, new TriggerBuildRequest(
            "pkg.spec", "spec-content", null, "webhook",
            CommitSha: "abc123", Branch: "main", CommitMessage: "fix", CommitAuthor: "jane"));

        Assert.Equal(pipeline.Id, job.PipelineId);
        Assert.Equal(BuildStatus.Queued, job.Status);
        Assert.Equal("abc123", job.CommitSha);
        Assert.Equal("main", job.Branch);
        Assert.Equal("fix", job.CommitMessage);
        Assert.Equal("jane", job.CommitAuthor);
        Assert.Equal("pkg.spec", job.SpecName);
        Assert.Equal("fedora", job.TargetDistribution);
        Assert.Equal("44", job.TargetRelease);
        Assert.Equal("aarch64", job.TargetArchitecture);
        Assert.Equal("fedora-44-aarch64", job.BuildProfile);
        Assert.Single(job.StepRuns);
        Assert.Equal(StepStatus.Running, job.StepRuns[0].Status);
        Assert.Equal(StepType.Build, job.StepRuns[0].Type);

        // A build WAS launched with the launcher fake.
        Assert.True(sp.GetRequiredService<FakeBuildLauncher>().WasLaunched);
    }

    [Fact]
    public async Task TriggerBuildAsync_ReturnsExistingJob_ForRepeatedIdempotencyKey()
    {
        await using var sp = BuildServiceProvider(nameof(TriggerBuildAsync_ReturnsExistingJob_ForRepeatedIdempotencyKey));
        var engine = await NewEngineAsync(sp);
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret"), "ops");
        var request = new TriggerBuildRequest(
            "pkg.spec", "", null, "webhook", IdempotencyKey: "webhook:delivery-42");

        var first = await engine.TriggerBuildAsync(pipeline.Id, request);
        var second = await engine.TriggerBuildAsync(pipeline.Id, request);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, sp.GetRequiredService<FakeBuildLauncher>().LaunchCount);
    }

    [Fact]
    public async Task TriggerBuildAsync_AutoSynthesizesSourceUrl_FromGitConfig()
    {
        // When SourceUrl is omitted but the pipeline has a GitRepoUrl, the
        // engine synthesizes "git://<repo>#branch=<branch>&specPath=<spec>".
        // This URL is what the build container clones, so a regression here
        // changes which repo gets built.
        await using var sp = BuildServiceProvider(nameof(TriggerBuildAsync_AutoSynthesizesSourceUrl_FromGitConfig));
        var engine = await NewEngineAsync(sp);
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret"), "ops");

        var launcher = sp.GetRequiredService<FakeBuildLauncher>();
        await engine.TriggerBuildAsync(pipeline.Id, new TriggerBuildRequest("pkg.spec", "", null, "ops"));

        Assert.NotNull(launcher.LastSourceUrl);
        Assert.Contains("git://example.com/repo.git", launcher.LastSourceUrl);
        Assert.Contains("branch=main", launcher.LastSourceUrl);
        Assert.Contains("specPath=", launcher.LastSourceUrl);
    }

    [Fact]
    public async Task TriggerBuildAsync_ThrowsNotFound_ForMissingPipeline()
    {
        await using var sp = BuildServiceProvider(nameof(TriggerBuildAsync_ThrowsNotFound_ForMissingPipeline));
        var engine = await NewEngineAsync(sp);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            engine.TriggerBuildAsync(Guid.NewGuid(), new TriggerBuildRequest("pkg.spec", "", null, "ops")));
    }

    [Fact]
    public async Task TriggerBuildAsync_FailsBuildStep_WhenLauncherThrows()
    {
        await using var sp = BuildServiceProvider(nameof(TriggerBuildAsync_FailsBuildStep_WhenLauncherThrows));
        var engine = await NewEngineAsync(sp);
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret"), "ops");
        sp.GetRequiredService<FakeBuildLauncher>().ThrowOnNextLaunch = new InvalidOperationException("docker down");

        var job = await engine.TriggerBuildAsync(pipeline.Id, new TriggerBuildRequest("pkg.spec", "", null, "ops"));

        Assert.Equal(BuildStatus.Failed, job.Status);
        Assert.Equal(StepStatus.Failed, job.StepRuns.Single().Status);
    }

    [Fact]
    public async Task TriggerBuildAsync_SnapshotsSelectedExecutionBackend()
    {
        await using var sp = BuildServiceProvider(nameof(TriggerBuildAsync_SnapshotsSelectedExecutionBackend));
        var engine = await NewEngineAsync(sp);
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret"), "ops");
        sp.GetRequiredService<FakeBuildLauncher>().Backend = BuildExecutorBackend.Kubernetes;

        var job = await engine.TriggerBuildAsync(
            pipeline.Id,
            new TriggerBuildRequest("pkg.spec", "Name: pkg", null, "ops"));

        Assert.Equal(BuildExecutorBackend.Kubernetes, job.ExecutionBackend);
    }

    [Fact]
    public async Task TriggerProjectBuildAsync_BindsDispatchBeforeLauncherRuns()
    {
        await using var sp = BuildServiceProvider(
            nameof(TriggerProjectBuildAsync_BindsDispatchBeforeLauncherRuns));
        var engine = await NewEngineAsync(sp);
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret"), "ops");
        var deliveryId = Guid.NewGuid();
        var binding = new PipelineEngine.ProjectBuildBinding(deliveryId, "kernel", 2);

        var job = await engine.TriggerProjectBuildAsync(
            pipeline.Id,
            new TriggerBuildRequest(
                "kernel.spec",
                string.Empty,
                "git://https://example.com/repo.git#branch=main&specPath=kernel.spec",
                $"project:{deliveryId:N}",
                new string('a', 40),
                "main",
                IdempotencyKey: $"project:{deliveryId:N}:kernel"),
            binding);

        var launched = Assert.IsType<BuildJob>(
            sp.GetRequiredService<FakeBuildLauncher>().LastJob);
        Assert.Same(job, launched);
        Assert.Equal(deliveryId, launched.ProjectWebhookDeliveryId);
        Assert.Equal("kernel", launched.ProjectPackageId);
        Assert.Equal(2, launched.ProjectStageOrder);
    }

    // ─── TriggerAutoBuildAsync ───────────────────────────────────────────

    [Fact]
    public async Task TriggerAutoBuildAsync_RejectsPipelineWithoutGitUrl()
    {
        await using var sp = BuildServiceProvider(nameof(TriggerAutoBuildAsync_RejectsPipelineWithoutGitUrl));
        var engine = await NewEngineAsync(sp);
        // Create a pipeline directly with no GitRepoUrl.
        var db = sp.GetRequiredService<BuildDbContext>();
        var pipeline = new Pipeline
        {
            Id = Guid.NewGuid(),
            Name = "nogit",
            CreatedBy = "ops",
            Status = PipelineStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            WebhookSecret = "s",
            Steps = new List<PipelineStep>()
        };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ValidationException>(() =>
            engine.TriggerAutoBuildAsync(pipeline.Id, "auto"));
    }

    [Fact]
    public async Task TriggerAutoBuildAsync_SynthesizesGitUrlAndTriggers()
    {
        await using var sp = BuildServiceProvider(nameof(TriggerAutoBuildAsync_SynthesizesGitUrlAndTriggers));
        var engine = await NewEngineAsync(sp);
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret"), "ops");

        var launcher = sp.GetRequiredService<FakeBuildLauncher>();
        var job = await engine.TriggerAutoBuildAsync(pipeline.Id, "auto");

        Assert.Equal(BuildStatus.Queued, job.Status);
        Assert.NotNull(launcher.LastSourceUrl);
        Assert.StartsWith("git://example.com/repo.git", launcher.LastSourceUrl!);
    }

    [Fact]
    public async Task ListBuildJobsAsync_FiltersBeforePagination_AndStatsAreGlobal()
    {
        await using var sp = BuildServiceProvider(nameof(ListBuildJobsAsync_FiltersBeforePagination_AndStatsAreGlobal));
        var engine = await NewEngineAsync(sp);
        var db = sp.GetRequiredService<BuildDbContext>();
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret"), "ops");
        db.BuildJobs.AddRange(
            new BuildJob { Id = Guid.NewGuid(), PipelineId = pipeline.Id, SpecName = "ok", Status = BuildStatus.Success },
            new BuildJob { Id = Guid.NewGuid(), PipelineId = pipeline.Id, SpecName = "bad", Status = BuildStatus.Failed },
            new BuildJob { Id = Guid.NewGuid(), PipelineId = pipeline.Id, SpecName = "queued", Status = BuildStatus.Queued });
        await db.SaveChangesAsync();

        var (items, total) = await engine.ListBuildJobsAsync(1, 1, BuildStatus.Failed);
        var stats = await engine.GetBuildStatsAsync();

        Assert.Single(items);
        Assert.Equal(BuildStatus.Failed, items[0].Status);
        Assert.Equal(1, total);
        Assert.Equal((3, 1, 1), stats);
    }

    [Fact]
    public async Task ListPipelinesAsync_SearchesBeforePagination()
    {
        await using var sp = BuildServiceProvider(nameof(ListPipelinesAsync_SearchesBeforePagination));
        var engine = await NewEngineAsync(sp);
        var db = sp.GetRequiredService<BuildDbContext>();
        db.Pipelines.AddRange(
            new Pipeline { Id = Guid.NewGuid(), Name = "kernel", Description = "stable", CreatedBy = "ops" },
            new Pipeline { Id = Guid.NewGuid(), Name = "tools", Description = "nightly utilities", CreatedBy = "ops" });
        await db.SaveChangesAsync();

        var (items, total) = await engine.ListPipelinesAsync(1, 20, "nightly");

        Assert.Single(items);
        Assert.Equal("tools", items[0].Name);
        Assert.Equal(1, total);
    }

    // ─── Test doubles ────────────────────────────────────────────────────

    /// <summary>
    /// Records whether <see cref="StartBuildAsync"/> was called and what
    /// arguments it received, so tests can assert the engine invoked the
    /// launcher with the synthesized source URL. Optionally throws to simulate
    /// a Docker outage.
    /// </summary>
    public sealed class FakeBuildLauncher : IBuildLauncher
    {
        public BuildExecutorBackend Backend { get; set; } = BuildExecutorBackend.Docker;
        public bool WasLaunched { get; private set; }
        public int LaunchCount { get; private set; }
        public string? LastSourceUrl { get; private set; }
        public BuildJob? LastJob { get; private set; }
        public Exception? ThrowOnNextLaunch { get; set; }

        public Task<BuildJob> StartBuildAsync(BuildJob job, string? specContent, string? sourceUrl,
            string? buildImage = null, string? gitUsername = null, string? gitToken = null,
            string? extraSourcesPipelineDir = null)
        {
            WasLaunched = true;
            LaunchCount++;
            LastSourceUrl = sourceUrl;
            LastJob = job;
            var toThrow = ThrowOnNextLaunch;
            ThrowOnNextLaunch = null;
            if (toThrow is not null) throw toThrow;
            return Task.FromResult(job);
        }

        public void Reset()
        {
            Backend = BuildExecutorBackend.Docker;
            WasLaunched = false;
            LaunchCount = 0;
            LastSourceUrl = null;
            LastJob = null;
            ThrowOnNextLaunch = null;
        }
    }

    /// <summary>
    /// Signing-key gate that records consultation and either returns normally
    /// (active) or throws <see cref="ValidationException"/> (no active key).
    /// </summary>
    public sealed class RecordingSigningKeyGate : ISigningKeyGate
    {
        private readonly bool _keyActive;
        public RecordingSigningKeyGate(bool keyActive) => _keyActive = keyActive;
        public bool WasConsulted { get; private set; }

        public Task RequireActiveKeyAsync(CancellationToken cancellationToken = default)
        {
            WasConsulted = true;
            if (!_keyActive)
                throw new ValidationException(SigningKeyGate.NoActiveKeyMessage);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Gate that always throws — simulates the fail-closed "cannot confirm
    /// key" path. Per the <see cref="ISigningKeyGate"/> contract, any failure
    /// (SecurityService unreachable, bus fault, missing key) surfaces as a
    /// <see cref="ValidationException"/>; the production
    /// <see cref="SigningKeyGate"/> wraps non-Validation exceptions itself, so
    /// this fake throws <see cref="ValidationException"/> directly to honor
    /// the same contract.
    /// </summary>
    public sealed class ThrowingSigningKeyGate : ISigningKeyGate
    {
        public Task RequireActiveKeyAsync(CancellationToken cancellationToken = default)
            => throw new ValidationException(SigningKeyGate.SecurityServiceUnreachableMessage);
    }

    /// <summary>
    /// Minimal in-process IDistributedCache for RedisCacheService. We are
    /// testing the engine, not Redis; an in-memory store keeps the test free
    /// of external infrastructure (mirrors the existing Shared.Tests approach).
    /// </summary>
    private sealed class InMemoryDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _store = new();
        public byte[]? Get(string key) => _store.TryGetValue(key, out var v) ? v : null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _store[key] = value;
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        { _store[key] = value; return Task.CompletedTask; }
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => _store.TryRemove(key, out _);
        public Task RemoveAsync(string key, CancellationToken token = default) { _store.TryRemove(key, out _); return Task.CompletedTask; }
    }

    /// <summary>
    /// Test-only <see cref="BuildDbContext"/> that adds value converters for
    /// the PG-specific column types (<c>text[]</c> for <c>Pipeline.Tags</c>,
    /// <c>hstore</c> for <c>PipelineStep.Configuration</c>) so the EF Core
    /// InMemory provider — which has no notion of array/hstore columns — can
    /// persist them. The converters round-trip via JSON, which is faithful
    /// enough for engine-logic tests (we assert behavior, not column storage).
    /// The base <c>OnModelCreating</c> still runs, so the production model
    /// shape (tables, keys, navigations, value converters for encrypted
    /// secrets) is preserved.
    /// </summary>
    private sealed class TestBuildDbContext : BuildDbContext
    {
        public TestBuildDbContext(DbContextOptions<BuildDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // InMemory cannot map Dictionary/List natively (production uses
            // Npgsql's hstore/text[]). JSON converters make them persistable
            // for tests without changing the production model.
            modelBuilder.Entity<Pipeline>()
                .Property(p => p.Tags)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new());

            modelBuilder.Entity<PipelineStep>()
                .Property(p => p.Configuration)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null) ?? new());

            modelBuilder.Entity<BuildStepRun>()
                .Property(p => p.Configuration)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null) ?? new());
        }
    }
}
