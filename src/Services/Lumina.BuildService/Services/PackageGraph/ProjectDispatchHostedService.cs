using Lumina.BuildService.Data;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services.PackageGraph;

/// <summary>
/// Reconciles planned and running project deliveries from durable database
/// state. Polling is intentional: progress does not depend on an in-memory
/// callback or a completion message surviving a process restart.
/// </summary>
public sealed class ProjectDispatchHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<ProjectDispatchHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            await ReconcileAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        List<Guid> deliveryIds;
        List<Guid> failedDeliveryIds;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();
            deliveryIds = await db.ProjectWebhookDeliveries.AsNoTracking()
                .Where(delivery => delivery.Status == ProjectWebhookStatus.PlanReady ||
                                   delivery.Status == ProjectWebhookStatus.Dispatched)
                // The dispatcher refreshes UpdatedAt while a stage is active,
                // so the bounded page rotates instead of starving later work.
                .OrderBy(delivery => delivery.UpdatedAt)
                .Select(delivery => delivery.Id)
                .Take(100)
                .ToListAsync(cancellationToken);
            failedDeliveryIds = await db.ProjectWebhookDeliveries.AsNoTracking()
                .Where(delivery => delivery.Status == ProjectWebhookStatus.Failed &&
                    delivery.BuildJobs.Any(job =>
                        job.Status == BuildStatus.Queued || job.Status == BuildStatus.Building))
                .OrderBy(delivery => delivery.UpdatedAt)
                .Select(delivery => delivery.Id)
                .Take(100)
                .ToListAsync(cancellationToken);
        }

        foreach (var deliveryId in deliveryIds)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<ProjectDispatchService>();
                await dispatcher.AdvanceAsync(deliveryId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to reconcile project delivery {DeliveryId}", deliveryId);
            }
        }

        foreach (var deliveryId in failedDeliveryIds)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var failures = scope.ServiceProvider.GetRequiredService<ProjectDeliveryFailureService>();
                await failures.FailAsync(
                    deliveryId,
                    "project-delivery-failed",
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Failed to terminalize builds for failed project delivery {DeliveryId}",
                    deliveryId);
            }
        }
    }
}
