using Lumina.BuildService.Data;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

internal interface IKubernetesBuildCompletion
{
    Task CompleteAsync(
        Guid buildJobId,
        string? logs,
        bool succeeded,
        string? error,
        CancellationToken cancellationToken);
}

internal sealed class KubernetesBuildCompletion(
    BuildDbContext db,
    PipelineRunCoordinator pipelines,
    BuildExecutionCoordinator execution) : IKubernetesBuildCompletion
{
    public async Task CompleteAsync(
        Guid buildJobId,
        string? logs,
        bool succeeded,
        string? error,
        CancellationToken cancellationToken)
    {
        var job = await db.BuildJobs
            .Include(item => item.Artifacts)
            .Include(item => item.StepRuns)
            .SingleAsync(item => item.Id == buildJobId, cancellationToken);
        if (job.Status == BuildStatus.Cancelled)
            return;
        if (job.Status != BuildStatus.Building || job.LeaseOwner != execution.WorkerId)
            throw new BuildExecutorIdentityException("Kubernetes build monitor no longer owns its database lease.");

        if (logs != null)
            job.Logs = logs;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.LastHeartbeatAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (succeeded && job.Artifacts.Count > 0)
        {
            await pipelines.CompleteBuildStepAsync(buildJobId, cancellationToken);
            return;
        }

        await pipelines.FailStepAsync(
            buildJobId,
            StepType.Build,
            succeeded
                ? "Kubernetes build completed without imported RPM artifacts."
                : error ?? "Kubernetes build failed.",
            cancellationToken);
    }
}
