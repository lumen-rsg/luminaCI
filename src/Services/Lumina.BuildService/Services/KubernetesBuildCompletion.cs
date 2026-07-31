using Lumina.BuildService.Data;
using Lumina.Shared.Errors;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

internal interface IKubernetesBuildCompletion
{
    Task<bool> CompleteAsync(
        Guid buildJobId,
        string? logs,
        bool succeeded,
        string? error,
        CancellationToken cancellationToken);
}

internal sealed class KubernetesBuildCompletion(
    BuildDbContext db,
    PipelineRunCoordinator pipelines,
    BuildExecutionCoordinator execution,
    IKubernetesArtifactImporter artifacts) : IKubernetesBuildCompletion
{
    public async Task<bool> CompleteAsync(
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
            return false;
        if (job.Status != BuildStatus.Building || job.LeaseOwner != execution.WorkerId)
            throw new BuildExecutorIdentityException("Kubernetes build monitor no longer owns its database lease.");

        if (succeeded)
        {
            try
            {
                await artifacts.ImportAsync(buildJobId, cancellationToken);
            }
            catch (ValidationException exception)
            {
                succeeded = false;
                error = $"Kubernetes artifacts were rejected: {exception.Message}";
            }
        }

        if (logs != null)
            job.Logs = logs;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.LastHeartbeatAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (succeeded && job.Artifacts.Count > 0)
        {
            await pipelines.CompleteBuildStepAsync(buildJobId, cancellationToken);
            return true;
        }

        await pipelines.FailStepAsync(
            buildJobId,
            StepType.Build,
            succeeded
                ? "Kubernetes build completed without imported RPM artifacts."
                : error ?? "Kubernetes build failed.",
            cancellationToken);
        return false;
    }
}
