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
            ? new[] { new CreatePipelineStepRequest(StepType.Sign, "sign", 2, new Dictionary<string, string>()) }
            : Array.Empty<CreatePipelineStepRequest>()).ToList(),
        Tags: new List<string>(),
        GitRepoUrl: "example.com/repo.git",
        GitBranch: "main",
        WebhookSecret: webhookSecret);

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
            "changed", "updated", [], [],
            ExpectedUpdatedAt: pipeline.UpdatedAt);

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

        // A build WAS launched with the launcher fake.
        Assert.True(sp.GetRequiredService<FakeBuildLauncher>().WasLaunched);
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
    public async Task TriggerBuildAsync_KeepsQueuedStatus_WhenLauncherThrows()
    {
        // The launcher is best-effort: if Docker is down, the job is still
        // persisted as Queued so a worker can retry, rather than vanishing.
        await using var sp = BuildServiceProvider(nameof(TriggerBuildAsync_KeepsQueuedStatus_WhenLauncherThrows));
        var engine = await NewEngineAsync(sp);
        var pipeline = await engine.CreatePipelineAsync(BuildRequest("s3cret"), "ops");
        sp.GetRequiredService<FakeBuildLauncher>().ThrowOnNextLaunch = new InvalidOperationException("docker down");

        var job = await engine.TriggerBuildAsync(pipeline.Id, new TriggerBuildRequest("pkg.spec", "", null, "ops"));

        Assert.Equal(BuildStatus.Queued, job.Status);
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

    // ─── Test doubles ────────────────────────────────────────────────────

    /// <summary>
    /// Records whether <see cref="StartBuildAsync"/> was called and what
    /// arguments it received, so tests can assert the engine invoked the
    /// launcher with the synthesized source URL. Optionally throws to simulate
    /// a Docker outage.
    /// </summary>
    public sealed class FakeBuildLauncher : IBuildLauncher
    {
        public bool WasLaunched { get; private set; }
        public string? LastSourceUrl { get; private set; }
        public Exception? ThrowOnNextLaunch { get; set; }

        public Task<BuildJob> StartBuildAsync(BuildJob job, string? specContent, string? sourceUrl,
            string? buildImage = null, string? gitUsername = null, string? gitToken = null,
            string? extraSourcesPipelineDir = null)
        {
            WasLaunched = true;
            LastSourceUrl = sourceUrl;
            var toThrow = ThrowOnNextLaunch;
            ThrowOnNextLaunch = null;
            if (toThrow is not null) throw toThrow;
            return Task.FromResult(job);
        }

        public void Reset() { WasLaunched = false; LastSourceUrl = null; ThrowOnNextLaunch = null; }
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
        }
    }
}
