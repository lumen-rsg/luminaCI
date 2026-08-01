using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services.PackageGraph;

/// <summary>
/// Makes project-delivery failure terminal for every build that belongs to the
/// delivery. Executor work is cancelled when the Build step is still running;
/// post-build steps are closed directly so late broker acknowledgements cannot
/// revive an already failed delivery.
/// </summary>
public sealed class ProjectDeliveryFailureService(
    BuildDbContext db,
    IBuildExecutorResolver executors,
    ILogger<ProjectDeliveryFailureService> logger)
{
    public async Task<bool> FailAsync(
        Guid deliveryId,
        string failureCode,
        CancellationToken cancellationToken,
        bool saveChanges = true)
    {
        if (string.IsNullOrWhiteSpace(failureCode) || failureCode.Length > 64)
            throw new ArgumentException("A bounded delivery failure code is required.", nameof(failureCode));

        var delivery = await db.ProjectWebhookDeliveries
            .Include(item => item.BuildJobs)
            .ThenInclude(job => job.StepRuns)
            .SingleOrDefaultAsync(item => item.Id == deliveryId, cancellationToken);
        if (delivery is null || delivery.Status is ProjectWebhookStatus.Completed or ProjectWebhookStatus.Ignored)
            return false;

        var effectiveFailureCode = delivery.Status == ProjectWebhookStatus.Failed &&
                                   !string.IsNullOrWhiteSpace(delivery.FailureCode)
            ? delivery.FailureCode
            : failureCode;

        var activeJobs = delivery.BuildJobs
            .Where(job => job.Status is BuildStatus.Queued or BuildStatus.Building)
            .ToList();
        foreach (var job in activeJobs.Where(HasRunningBuildStep))
        {
            try
            {
                await executors.Resolve(job.ExecutionBackend)
                    .CancelBuildAsync(job.Id, cancellationToken);
            }
            catch (Exception exception)
            {
                // The database transition below remains fail-closed: a late
                // executor result sees Cancelled and cannot advance the run.
                // Kubernetes Jobs also retain their administrator TTL cleanup.
                logger.LogError(
                    exception,
                    "Failed to stop executor work for build {BuildJobId} in failed delivery {DeliveryId}",
                    job.Id,
                    delivery.Id);
            }
        }

        var now = DateTime.UtcNow;
        var reason = $"Project delivery failed ({effectiveFailureCode}).";
        foreach (var job in activeJobs.Where(job =>
                     job.Status is BuildStatus.Queued or BuildStatus.Building))
        {
            job.Status = BuildStatus.Cancelled;
            job.CompletedAt = now;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            foreach (var step in job.StepRuns.Where(step =>
                         step.Status is StepStatus.Pending or StepStatus.Running))
            {
                step.Status = StepStatus.Skipped;
                step.CompletedAt = now;
                step.Error = reason;
            }
        }

        delivery.Status = ProjectWebhookStatus.Failed;
        delivery.FailureCode = effectiveFailureCode;
        delivery.UpdatedAt = now;
        if (saveChanges)
            await db.SaveChangesAsync(cancellationToken);

        if (activeJobs.Count > 0)
        {
            logger.LogWarning(
                "Terminalized {BuildCount} active builds for failed project delivery {DeliveryId}",
                activeJobs.Count,
                delivery.Id);
        }
        return true;
    }

    private static bool HasRunningBuildStep(BuildJob job) =>
        job.Status == BuildStatus.Building &&
        job.StepRuns.Any(step => step.Type == StepType.Build && step.Status == StepStatus.Running);
}
