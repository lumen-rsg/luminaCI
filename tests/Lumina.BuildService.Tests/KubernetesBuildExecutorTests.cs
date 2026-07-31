using k8s.Models;
using System.Text.Json;
using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class KubernetesBuildExecutorTests
{
    private const string Digest =
        "registry.example/lumina/fedora-runner@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task MonitorBuildAsync_RecoversCreateIntentAndCompletesObservedJob()
    {
        var resources = new FakeResources
        {
            Observation = new KubernetesBuildObservation(
                KubernetesBuildPhase.Succeeded,
                "pod-1",
                "Completed",
                DateTimeOffset.UtcNow),
            Logs = "build output"
        };
        var completion = new FakeCompletion();
        using var services = Services(resources, completion);
        var job = Job(withUid: false);
        var execution = services.GetRequiredService<BuildExecutionCoordinator>();
        job.LeaseOwner = execution.WorkerId;
        await SeedAsync(services, job);
        await execution.AcquireAsync(job.Id, recovered: true);
        var logStreams = services.GetRequiredService<IBuildLogStreamHub>();
        logStreams.Start(job.Id);
        var subscription = await logStreams.SubscribeAsync(job.Id);

        await using (var scope = services.CreateAsyncScope())
        {
            var executor = Executor(scope.ServiceProvider, resources, execution);
            await executor.MonitorBuildAsync(job);
        }

        await using var verificationScope = services.CreateAsyncScope();
        var persisted = await verificationScope.ServiceProvider
            .GetRequiredService<BuildDbContext>()
            .BuildJobs.SingleAsync(item => item.Id == job.Id);
        Assert.Equal("job-uid-1", persisted.KubernetesJobUid);
        Assert.Equal("pod-1", persisted.KubernetesPodName);
        Assert.True(resources.EnsureCalled);
        Assert.True(resources.ActivateCalled);
        Assert.True(resources.DeleteCalled);
        Assert.Equal((job.Id, true, "build output", "Completed"), completion.Call);
        Assert.Equal("build output", await subscription.Reader!.ReadAsync());
        Assert.Equal("[BUILD SUCCESS]", await subscription.Reader.ReadAsync());
        logStreams.Unsubscribe(job.Id, subscription.Reader);
    }

    [Fact]
    public async Task CancelBuildAsync_DeletesRecordedUidAndSkipsActiveSteps()
    {
        var resources = new FakeResources();
        var completion = new FakeCompletion();
        using var services = Services(resources, completion);
        var job = Job(withUid: true);
        await SeedAsync(services, job);
        var execution = services.GetRequiredService<BuildExecutionCoordinator>();

        await using (var scope = services.CreateAsyncScope())
        {
            var executor = Executor(scope.ServiceProvider, resources, execution);
            Assert.True(await executor.CancelBuildAsync(job.Id));
        }

        await using var verificationScope = services.CreateAsyncScope();
        var persisted = await verificationScope.ServiceProvider
            .GetRequiredService<BuildDbContext>()
            .BuildJobs.Include(item => item.StepRuns)
            .SingleAsync(item => item.Id == job.Id);
        Assert.Equal(BuildStatus.Cancelled, persisted.Status);
        Assert.All(persisted.StepRuns, step => Assert.Equal(StepStatus.Skipped, step.Status));
        Assert.Equal("job-uid-1", resources.DeletedIdentity?.JobUid);
        Assert.False(resources.EnsureCalled);
    }

    [Fact]
    public async Task MonitorBuildAsync_PublishesTrustedCompletionOutcome()
    {
        var resources = new FakeResources
        {
            Observation = new KubernetesBuildObservation(
                KubernetesBuildPhase.Succeeded,
                "pod-1",
                "Completed",
                DateTimeOffset.UtcNow),
            Logs = "build output"
        };
        var completion = new FakeCompletion { FinalResult = false };
        using var services = Services(resources, completion);
        var job = Job(withUid: true);
        var execution = services.GetRequiredService<BuildExecutionCoordinator>();
        job.LeaseOwner = execution.WorkerId;
        await SeedAsync(services, job);
        await execution.AcquireAsync(job.Id, recovered: true);
        var streams = services.GetRequiredService<IBuildLogStreamHub>();
        streams.Start(job.Id);
        var subscription = await streams.SubscribeAsync(job.Id);

        await using (var scope = services.CreateAsyncScope())
        {
            await Executor(scope.ServiceProvider, resources, execution).MonitorBuildAsync(job);
        }

        Assert.Equal((job.Id, true, "build output", "Completed"), completion.Call);
        Assert.Equal("build output", await subscription.Reader!.ReadAsync());
        Assert.Equal("[BUILD FAILED]", await subscription.Reader.ReadAsync());
        Assert.True(resources.DeleteCalled);
        streams.Unsubscribe(job.Id, subscription.Reader);
    }

    private static KubernetesBuildExecutor Executor(
        IServiceProvider services,
        IKubernetesBuildResourceClient resources,
        BuildExecutionCoordinator execution) => new(
        services.GetRequiredService<BuildDbContext>(),
        services.GetRequiredService<IConfiguration>(),
        new BuildExecutorSelection(BuildExecutorBackend.Kubernetes, "lumina-builds"),
        new FakeSlotClaimer(),
        resources,
        services.GetRequiredService<IKubernetesBuildTransportService>(),
        execution,
        services.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<KubernetesBuildExecutor>.Instance,
        services.GetRequiredService<IBuildLogStreamHub>());

    private static ServiceProvider Services(
        IKubernetesBuildResourceClient resources,
        IKubernetesBuildCompletion completion)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kubernetes:RunnerImages:fedora-44-aarch64"] = Digest,
                ["Kubernetes:Network:EgressCidr"] = "146.120.224.52/32",
                ["Kubernetes:Network:HttpsPort"] = "443",
                ["Kubernetes:Network:FedoraRepositoryBaseUrl"] = "https://packages.lumina.1t.ru/fedora",
                ["MinIO:RunnerEndpoint"] = "packages.lumina.1t.ru:443",
                ["MinIO:RunnerUseSSL"] = "true",
                ["Kubernetes:Monitoring:PollSeconds"] = "2",
                ["BuildMonitoring:MaxConcurrentBuilds"] = "4",
                ["BuildMonitoring:LeaseSeconds"] = "60",
                ["BuildMonitoring:MaxBuildMinutes"] = "120"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<BuildExecutionCoordinator>();
        services.AddSingleton<IBuildLogStreamHub, BuildLogStreamHub>();
        services.AddSingleton(resources);
        services.AddSingleton(completion);
        services.AddSingleton<IKubernetesBuildTransportService, FakeTransportService>();
        var options = new DbContextOptionsBuilder<BuildDbContext>()
            .UseInMemoryDatabase($"kubernetes-executor-{Guid.NewGuid():N}")
            .Options;
        services.AddScoped<BuildDbContext>(_ => new TestBuildDbContext(options));
        return services.BuildServiceProvider();
    }

    private static async Task SeedAsync(IServiceProvider services, BuildJob job)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();
        db.BuildJobs.Add(job);
        await db.SaveChangesAsync();
    }

    private static BuildJob Job(bool withUid)
    {
        var id = Guid.NewGuid();
        return new BuildJob
        {
            Id = id,
            PipelineId = Guid.NewGuid(),
            ExecutionBackend = BuildExecutorBackend.Kubernetes,
            Status = BuildStatus.Building,
            SpecName = "kernel-tegra.spec",
            SourceUrl = "git://https://example.com/lumina.git#branch=main&specPath=jetson/kernel-tegra.spec&commit=" + new string('b', 40),
            BuildProfile = "fedora-44-aarch64",
            TargetDistribution = "fedora",
            TargetRelease = "44",
            TargetArchitecture = "aarch64",
            RunnerImageReference = Digest,
            RunnerImageDigest = "sha256:" + new string('a', 64),
            KubernetesNamespace = "lumina-builds",
            KubernetesJobName = KubernetesBuildIdentity.JobName(id),
            KubernetesJobUid = withUid ? "job-uid-1" : null,
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(1),
            DeadlineAt = DateTime.UtcNow.AddHours(1),
            StepRuns =
            [
                new BuildStepRun
                {
                    Id = Guid.NewGuid(),
                    Type = StepType.Build,
                    Name = "Build",
                    Order = 0,
                    Status = StepStatus.Running
                }
            ]
        };
    }

    private sealed class FakeSlotClaimer : IBuildSlotClaimer
    {
        public Task ClaimAsync(BuildJob job, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TestBuildDbContext(DbContextOptions<BuildDbContext> options)
        : BuildDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Pipeline>().Property(item => item.Tags).HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<List<string>>(value, (JsonSerializerOptions?)null) ?? new List<string>());
            ConfigureDictionary(modelBuilder.Entity<PipelineStep>().Property(item => item.Configuration));
            ConfigureDictionary(modelBuilder.Entity<BuildStepRun>().Property(item => item.Configuration));
        }

        private static void ConfigureDictionary(
            PropertyBuilder<Dictionary<string, string>> property) => property.HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<Dictionary<string, string>>(
                    value,
                    (JsonSerializerOptions?)null) ?? new Dictionary<string, string>());
    }

    private sealed class FakeCompletion : IKubernetesBuildCompletion
    {
        public (Guid Id, bool Succeeded, string? Logs, string? Error)? Call { get; private set; }
        public bool? FinalResult { get; init; }

        public Task<bool> CompleteAsync(
            Guid buildJobId,
            string? logs,
            bool succeeded,
            string? error,
            CancellationToken cancellationToken)
        {
            Call = (buildJobId, succeeded, logs, error);
            return Task.FromResult(FinalResult ?? succeeded);
        }
    }

    private sealed class FakeResources : IKubernetesBuildResourceClient
    {
        public KubernetesBuildObservation Observation { get; set; } = new(
            KubernetesBuildPhase.Pending,
            null,
            null,
            DateTimeOffset.UtcNow);
        public string Logs { get; set; } = string.Empty;
        public bool EnsureCalled { get; private set; }
        public bool DeleteCalled { get; private set; }
        public bool ActivateCalled { get; private set; }
        public KubernetesBuildResourceIdentity? DeletedIdentity { get; private set; }

        public Task<KubernetesBuildResourceIdentity> EnsureCreatedAsync(
            string buildNamespace,
            V1Job job,
            V1NetworkPolicy networkPolicy,
            CancellationToken cancellationToken)
        {
            EnsureCalled = true;
            return Task.FromResult(new KubernetesBuildResourceIdentity(
                buildNamespace,
                job.Metadata.Name,
                "job-uid-1",
                null));
        }

        public Task<KubernetesBuildObservation> ObserveAsync(
            KubernetesBuildResourceIdentity identity,
            CancellationToken cancellationToken) => Task.FromResult(Observation);

        public Task ActivateAsync(
            KubernetesBuildResourceIdentity identity,
            V1Secret transportSecret,
            KubernetesBuildTransport requestedTransport,
            CancellationToken cancellationToken)
        {
            ActivateCalled = true;
            return Task.CompletedTask;
        }

        public Task<string> ReadLogsAsync(
            KubernetesBuildResourceIdentity identity,
            int maximumCharacters,
            CancellationToken cancellationToken) => Task.FromResult(Logs);

        public Task DeleteAsync(
            KubernetesBuildResourceIdentity identity,
            CancellationToken cancellationToken)
        {
            DeleteCalled = true;
            DeletedIdentity = identity;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTransportService : IKubernetesBuildTransportService
    {
        public Task<KubernetesBuildTransport> PrepareAsync(
            Guid buildJobId,
            KubernetesBuildResourceIdentity identity,
            KubernetesJobLimits limits,
            CancellationToken cancellationToken) => Task.FromResult(new KubernetesBuildTransport(
            KubernetesBuildTransportPolicy.CurrentVersion,
            buildJobId,
            identity.JobUid,
            "project/source/verified.tar.gz",
            new string('a', 64),
            4096,
            "https://minio.example/snapshot?signature=download",
            [],
            KubernetesBuildTransportPolicy.BundleObjectName(buildJobId, identity.JobUid),
            "https://minio.example/bundle?signature=upload",
            DateTimeOffset.UtcNow.AddHours(1)));
    }
}
