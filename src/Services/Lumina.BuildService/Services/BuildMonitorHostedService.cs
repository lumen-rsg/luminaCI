using Lumina.BuildService.Data;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

/// <summary>
/// Reclaims expired build leases after startup and resumes monitoring their
/// Docker containers. The database claim makes adoption safe across replicas.
/// </summary>
public sealed class BuildMonitorHostedService(
    IServiceScopeFactory scopeFactory,
    BuildExecutionCoordinator execution,
    ILogger<BuildMonitorHostedService> logger) : BackgroundService
{
    private readonly Dictionary<Guid, Task> _monitors = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            await ReconcileAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        foreach (var completed in _monitors.Where(item => item.Value.IsCompleted).Select(item => item.Key).ToList())
            _monitors.Remove(completed);

        await using var queryScope = scopeFactory.CreateAsyncScope();
        var db = queryScope.ServiceProvider.GetRequiredService<BuildDbContext>();
        var now = DateTime.UtcNow;
        var candidates = await db.BuildJobs
            .AsNoTracking()
            .Where(job => job.Status == BuildStatus.Building
                && job.StepRuns.Any(step =>
                    step.Type == StepType.Build && step.Status == StepStatus.Running)
                && (job.LeaseExpiresAt == null || job.LeaseExpiresAt < now))
            .Select(job => job.Id)
            .ToListAsync(cancellationToken);

        foreach (var jobId in candidates)
        {
            if (_monitors.ContainsKey(jobId))
                continue;

            var claimed = await db.BuildJobs
                .Where(job => job.Id == jobId
                    && job.Status == BuildStatus.Building
                    && job.StepRuns.Any(step =>
                        step.Type == StepType.Build && step.Status == StepStatus.Running)
                    && (job.LeaseExpiresAt == null || job.LeaseExpiresAt < now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.LeaseOwner, execution.WorkerId)
                    .SetProperty(job => job.LastHeartbeatAt, now)
                    .SetProperty(job => job.LeaseExpiresAt, now + execution.LeaseDuration)
                    .SetProperty(
                        job => job.DeadlineAt,
                        job => job.DeadlineAt ?? now + execution.MaxBuildDuration),
                    cancellationToken);

            if (claimed == 1)
            {
                logger.LogWarning("Reclaimed expired monitor lease for build {BuildJobId}", jobId);
                _monitors[jobId] = MonitorRecoveredAsync(jobId, cancellationToken);
            }
        }
    }

    private async Task MonitorRecoveredAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();
            var job = await db.BuildJobs.AsNoTracking().SingleAsync(item => item.Id == jobId, cancellationToken);
            if (string.IsNullOrWhiteSpace(job.ContainerId))
            {
                var coordinator = scope.ServiceProvider.GetRequiredService<PipelineRunCoordinator>();
                await coordinator.FailStepAsync(
                    jobId,
                    StepType.Build,
                    "Build monitoring was interrupted before a container was recorded.",
                    cancellationToken);
                return;
            }

            var builds = scope.ServiceProvider.GetRequiredService<DockerBuildService>();
            await execution.AcquireAsync(jobId, recovered: true, cancellationToken);
            await builds.MonitorBuildAsync(job, job.ContainerId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recover build monitor for job {BuildJobId}", jobId);
            execution.Release(jobId);
        }
    }
}
