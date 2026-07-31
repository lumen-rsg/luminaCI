using Lumina.BuildService.Data;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

/// <summary>
/// Owns the durable Kubernetes Job lifecycle. Repository data selects package
/// inputs only; runner image, namespace, resources, scheduling, and manifests
/// remain administrator-controlled.
/// </summary>
public sealed class KubernetesBuildExecutor : IBuildExecutor
{
    private const int MaximumLogCharacters = 1_000_000;

    private readonly BuildDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly BuildExecutorSelection _selection;
    private readonly IBuildSlotClaimer _slotClaimer;
    private readonly IKubernetesBuildResourceClient _resources;
    private readonly IKubernetesBuildTransportService _transport;
    private readonly BuildExecutionCoordinator _execution;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KubernetesBuildExecutor> _logger;
    private readonly IBuildLogStreamHub _logStreams;
    private readonly TimeSpan _pollInterval;

    public KubernetesBuildExecutor(
        BuildDbContext db,
        IConfiguration configuration,
        BuildExecutorSelection selection,
        IBuildSlotClaimer slotClaimer,
        IKubernetesBuildResourceClient resources,
        IKubernetesBuildTransportService transport,
        BuildExecutionCoordinator execution,
        IServiceScopeFactory scopeFactory,
        ILogger<KubernetesBuildExecutor> logger,
        IBuildLogStreamHub logStreams)
    {
        _db = db;
        _configuration = configuration;
        _selection = selection;
        _slotClaimer = slotClaimer;
        _resources = resources;
        _transport = transport;
        _execution = execution;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _logStreams = logStreams;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(
            2,
            configuration.GetValue("Kubernetes:Monitoring:PollSeconds", 5)));
    }

    public BuildExecutorBackend Backend => BuildExecutorBackend.Kubernetes;

    public async Task<BuildJob> StartBuildAsync(
        BuildJob job,
        string? specContent,
        string? sourceUrl,
        string? buildImage = null,
        string? gitUsername = null,
        string? gitToken = null,
        string? extraSourcesPipelineDir = null)
    {
        if (_selection.Backend != BuildExecutorBackend.Kubernetes ||
            string.IsNullOrWhiteSpace(_selection.KubernetesNamespace))
        {
            throw new InvalidOperationException("Kubernetes is not selected for new builds.");
        }
        if (job.ExecutionBackend != BuildExecutorBackend.Kubernetes)
            throw new BuildExecutorIdentityException("Kubernetes executor cannot start a non-Kubernetes build.");
        BuildSourceSecurityPolicy.EnsureCredentialFree(sourceUrl, gitUsername, gitToken);
        if (!string.IsNullOrWhiteSpace(extraSourcesPipelineDir))
        {
            throw new ValidationException(
                "Kubernetes builds require extra sources to be staged in the immutable repository snapshot.");
        }

        var runner = KubernetesBuildPolicy.ResolveRunner(_configuration, job, buildImage);
        var limits = KubernetesBuildPolicy.ResolveLimits(_configuration);
        job.RunnerImageReference = runner.Image;
        job.RunnerImageDigest = runner.Image[(runner.Image.IndexOf('@') + 1)..];
        job.KubernetesNamespace = _selection.KubernetesNamespace;
        job.KubernetesJobName = KubernetesBuildIdentity.JobName(job.Id);
        await _db.SaveChangesAsync();

        var ownsExecutionSlot = false;
        KubernetesBuildResourceIdentity? createdIdentity = null;
        try
        {
            await _execution.AcquireAsync(job.Id, recovered: false);
            ownsExecutionSlot = true;
            await _slotClaimer.ClaimAsync(job);

            createdIdentity = await EnsureIdentityAsync(job, runner, limits, CancellationToken.None);
            KubernetesBuildIdentity.Apply(job, createdIdentity);
            await _db.SaveChangesAsync();
            await ActivateAsync(job.Id, createdIdentity, limits, CancellationToken.None);

            _logger.LogInformation(
                "Started Kubernetes Job {Namespace}/{JobName} ({JobUid}) for build {BuildJobId}",
                createdIdentity.Namespace,
                createdIdentity.JobName,
                createdIdentity.JobUid,
                job.Id);
            _logStreams.Start(job.Id, job.Logs ?? string.Empty);
            _ = ObserveMonitorAsync(job.Id);
            return job;
        }
        catch
        {
            if (createdIdentity != null)
            {
                try
                {
                    await _resources.DeleteAsync(createdIdentity, CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    _logger.LogCritical(
                        cleanupException,
                        "Failed to remove Kubernetes Job after launch failure for build {BuildJobId}",
                        job.Id);
                }
            }
            job.Status = BuildStatus.Failed;
            job.CompletedAt = DateTime.UtcNow;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            await _db.SaveChangesAsync();
            if (ownsExecutionSlot)
                _execution.Release(job.Id);
            throw;
        }
    }

    public async Task MonitorBuildAsync(
        BuildJob job,
        CancellationToken cancellationToken = default)
    {
        if (job.ExecutionBackend != BuildExecutorBackend.Kubernetes)
            throw new BuildExecutorIdentityException("Kubernetes executor cannot monitor a non-Kubernetes build.");

        _logStreams.Start(job.Id, job.Logs ?? string.Empty);
        try
        {
            var identity = await RecoverIdentityAsync(job.Id, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                var observation = await _resources.ObserveAsync(identity, cancellationToken);
                identity = identity with { PodName = observation.PodName ?? identity.PodName };
                string? logs = null;
                if (!string.IsNullOrWhiteSpace(identity.PodName))
                {
                    try
                    {
                        logs = await _resources.ReadLogsAsync(
                            identity,
                            MaximumLogCharacters,
                            cancellationToken);
                    }
                    catch (NotFoundException ex)
                    {
                        _logger.LogDebug(ex, "Kubernetes logs are not yet available for build {BuildJobId}", job.Id);
                    }
                }
                if (logs != null)
                    _logStreams.UpdateSnapshot(job.Id, logs);

                var heartbeat = await HeartbeatAsync(
                    job.Id,
                    identity.PodName,
                    logs,
                    cancellationToken);
                if (heartbeat == MonitorHeartbeat.LeaseLost)
                {
                    _logger.LogWarning("Kubernetes monitor lost its lease for build {BuildJobId}", job.Id);
                    return;
                }
                if (heartbeat == MonitorHeartbeat.Cancelled)
                    return;

                if (observation.Phase is KubernetesBuildPhase.Succeeded or KubernetesBuildPhase.Failed)
                {
                    await CompleteAsync(
                        job.Id,
                        identity,
                        logs,
                        observation.Phase == KubernetesBuildPhase.Succeeded,
                        observation.Reason,
                        cancellationToken);
                    return;
                }

                var deadline = await ReadDeadlineAsync(job.Id, cancellationToken);
                if (deadline <= DateTime.UtcNow)
                {
                    await CompleteAsync(
                        job.Id,
                        identity,
                        logs,
                        false,
                        $"Kubernetes build exceeded its {_execution.MaxBuildDuration} wall-clock limit.",
                        cancellationToken);
                    return;
                }

                await Task.Delay(_pollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Kubernetes monitor for build {BuildJobId} is stopping; its lease will be reclaimed",
                job.Id);
        }
        catch (DomainException ex)
        {
            _logger.LogError(ex, "Kubernetes resource identity failed for build {BuildJobId}", job.Id);
            _logStreams.Publish(job.Id, "[BUILD ERROR - Kubernetes resource identity failed]");
            await FailOwnedBuildBestEffortAsync(job.Id, ex.Message);
        }
        finally
        {
            _execution.Release(job.Id);
            _logStreams.Complete(job.Id);
        }
    }

    public async Task<bool> CancelBuildAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await _db.BuildJobs
            .Include(item => item.StepRuns)
            .SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job == null || job.ExecutionBackend != BuildExecutorBackend.Kubernetes ||
            job.Status is not (BuildStatus.Queued or BuildStatus.Building))
        {
            return false;
        }

        if (job.Status == BuildStatus.Building)
        {
            var runner = KubernetesBuildPolicy.ResolveRunner(
                _configuration,
                job,
                job.RunnerImageReference);
            var identity = await EnsureIdentityAsync(
                job,
                runner,
                KubernetesBuildPolicy.ResolveLimits(_configuration),
                cancellationToken);
            KubernetesBuildIdentity.Apply(job, identity);
            await _db.SaveChangesAsync(cancellationToken);
            await _resources.DeleteAsync(identity, cancellationToken);
        }

        job.Status = BuildStatus.Cancelled;
        job.CompletedAt = DateTime.UtcNow;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        foreach (var step in job.StepRuns.Where(step =>
                     step.Status is StepStatus.Pending or StepStatus.Running))
        {
            step.Status = StepStatus.Skipped;
            step.CompletedAt = DateTime.UtcNow;
            step.Error = "Build cancelled.";
        }
        await _db.SaveChangesAsync(cancellationToken);
        _logStreams.Publish(job.Id, "[BUILD CANCELLED]");
        _logStreams.Complete(job.Id);
        _execution.Release(job.Id);
        return true;
    }

    private async Task<KubernetesBuildResourceIdentity> RecoverIdentityAsync(
        Guid buildJobId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();
        var job = await db.BuildJobs.SingleAsync(item => item.Id == buildJobId, cancellationToken);
        if (job.LeaseOwner != _execution.WorkerId || job.Status != BuildStatus.Building)
            throw new BuildExecutorIdentityException("Kubernetes build monitor does not own the active lease.");
        var runner = KubernetesBuildPolicy.ResolveRunner(
            _configuration,
            job,
            job.RunnerImageReference);
        var identity = await EnsureIdentityAsync(
            job,
            runner,
            KubernetesBuildPolicy.ResolveLimits(_configuration),
            cancellationToken);
        KubernetesBuildIdentity.Apply(job, identity);
        await db.SaveChangesAsync(cancellationToken);
        await ActivateAsync(
            job.Id,
            identity,
            KubernetesBuildPolicy.ResolveLimits(_configuration),
            cancellationToken);
        return identity with { PodName = job.KubernetesPodName ?? identity.PodName };
    }

    private async Task<KubernetesBuildResourceIdentity> EnsureIdentityAsync(
        BuildJob job,
        KubernetesRunner runner,
        KubernetesJobLimits limits,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(job.KubernetesJobUid))
        {
            if (string.IsNullOrWhiteSpace(job.KubernetesNamespace) ||
                string.IsNullOrWhiteSpace(job.KubernetesJobName))
            {
                throw new BuildExecutorIdentityException("Kubernetes build resource identity is incomplete.");
            }
            return new KubernetesBuildResourceIdentity(
                job.KubernetesNamespace,
                job.KubernetesJobName,
                job.KubernetesJobUid,
                job.KubernetesPodName);
        }
        if (string.IsNullOrWhiteSpace(job.KubernetesNamespace) ||
            !string.Equals(job.KubernetesJobName, KubernetesBuildIdentity.JobName(job.Id), StringComparison.Ordinal))
        {
            throw new BuildExecutorIdentityException("Kubernetes build creation intent was not recorded.");
        }

        return await _resources.EnsureCreatedAsync(
            job.KubernetesNamespace,
            KubernetesJobFactory.Create(job, runner, limits),
            KubernetesJobFactory.CreateDefaultDenyNetworkPolicy(job),
            cancellationToken);
    }

    private async Task ActivateAsync(
        Guid buildJobId,
        KubernetesBuildResourceIdentity identity,
        KubernetesJobLimits limits,
        CancellationToken cancellationToken)
    {
        var transport = await _transport.PrepareAsync(
            buildJobId,
            identity,
            limits,
            cancellationToken);
        var secret = KubernetesBuildTransportPolicy.CreateSecret(
            identity.Namespace,
            identity.JobName,
            transport);
        await _resources.ActivateAsync(
            identity,
            secret,
            transport,
            cancellationToken);
    }

    private async Task<MonitorHeartbeat> HeartbeatAsync(
        Guid buildJobId,
        string? podName,
        string? logs,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();
        var job = await db.BuildJobs.SingleOrDefaultAsync(
            item => item.Id == buildJobId,
            cancellationToken);
        if (job == null || job.LeaseOwner != _execution.WorkerId)
            return MonitorHeartbeat.LeaseLost;
        if (job.Status == BuildStatus.Cancelled)
            return MonitorHeartbeat.Cancelled;

        job.KubernetesPodName = podName;
        if (logs != null)
            job.Logs = logs;
        job.LastHeartbeatAt = DateTime.UtcNow;
        job.LeaseExpiresAt = job.LastHeartbeatAt + _execution.LeaseDuration;
        await db.SaveChangesAsync(cancellationToken);
        return MonitorHeartbeat.Active;
    }

    private async Task<DateTime> ReadDeadlineAsync(
        Guid buildJobId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();
        return await db.BuildJobs
            .Where(item => item.Id == buildJobId)
            .Select(item => item.DeadlineAt ?? DateTime.UtcNow)
            .SingleAsync(cancellationToken);
    }

    private async Task CompleteAsync(
        Guid buildJobId,
        KubernetesBuildResourceIdentity identity,
        string? logs,
        bool succeeded,
        string? error,
        CancellationToken cancellationToken)
    {
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var completion = scope.ServiceProvider.GetRequiredService<IKubernetesBuildCompletion>();
            succeeded = await completion.CompleteAsync(
                buildJobId,
                logs,
                succeeded,
                error,
                cancellationToken);
        }
        _logStreams.Publish(buildJobId, succeeded ? "[BUILD SUCCESS]" : "[BUILD FAILED]");
        try
        {
            await _resources.DeleteAsync(identity, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up Kubernetes resources for build {BuildJobId}", buildJobId);
        }
    }

    private async Task FailOwnedBuildBestEffortAsync(Guid buildJobId, string error)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var completion = scope.ServiceProvider.GetRequiredService<IKubernetesBuildCompletion>();
            await completion.CompleteAsync(
                buildJobId,
                null,
                false,
                error,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist Kubernetes monitor failure for build {BuildJobId}", buildJobId);
        }
    }

    private async Task ObserveMonitorAsync(Guid buildJobId)
    {
        try
        {
            await MonitorBuildAsync(
                new BuildJob { Id = buildJobId, ExecutionBackend = BuildExecutorBackend.Kubernetes });
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Kubernetes monitor task escaped for build {BuildJobId}", buildJobId);
        }
    }

    private enum MonitorHeartbeat
    {
        Active,
        LeaseLost,
        Cancelled
    }
}
