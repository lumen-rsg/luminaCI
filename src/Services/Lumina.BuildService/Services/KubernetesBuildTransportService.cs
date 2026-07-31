using System.Text.Json;
using Lumina.BuildService.Data;
using Lumina.BuildService.Services.PackageGraph;
using Lumina.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

public interface IKubernetesBuildTransportService
{
    Task<KubernetesBuildTransport> PrepareAsync(
        Guid buildJobId,
        KubernetesBuildResourceIdentity identity,
        KubernetesJobLimits limits,
        CancellationToken cancellationToken);
}

internal sealed class KubernetesBuildTransportService(
    BuildDbContext db,
    IKubernetesObjectUrlSigner signer) : IKubernetesBuildTransportService
{
    public async Task<KubernetesBuildTransport> PrepareAsync(
        Guid buildJobId,
        KubernetesBuildResourceIdentity identity,
        KubernetesJobLimits limits,
        CancellationToken cancellationToken)
    {
        var job = await db.BuildJobs
            .AsNoTracking()
            .Include(item => item.ProjectWebhookDelivery)
            .SingleAsync(item => item.Id == buildJobId, cancellationToken);
        var delivery = job.ProjectWebhookDelivery
                       ?? throw new ValidationException(
                           "Kubernetes builds require a verified repository-project snapshot.");
        var lookasideSources = ResolveLookasideSources(job, delivery);
        return await KubernetesBuildTransportPolicy.CreateAsync(
            job,
            delivery,
            identity,
            limits,
            lookasideSources,
            signer,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    internal static IReadOnlyList<ProjectLookasideSource> ResolveLookasideSources(
        Lumina.Shared.Models.BuildJob job,
        Lumina.Shared.Models.ProjectWebhookDelivery delivery)
    {
        if (string.IsNullOrWhiteSpace(job.ProjectPackageId) ||
            string.IsNullOrWhiteSpace(delivery.DispatchPlanJson))
            throw new ValidationException("Kubernetes build has no persisted package dispatch plan.");
        ProjectDispatchPlan plan;
        try
        {
            plan = JsonSerializer.Deserialize<ProjectDispatchPlan>(
                       delivery.DispatchPlanJson,
                       new JsonSerializerOptions(JsonSerializerDefaults.Web))
                   ?? throw new JsonException("Dispatch plan is empty.");
        }
        catch (JsonException exception)
        {
            throw new ValidationException("Kubernetes build dispatch plan is invalid.", exception);
        }

        if (plan.Stages is null || plan.Stages.Any(stage => stage is null || stage.Targets is null))
            throw new ValidationException("Kubernetes build dispatch stages are invalid.");
        var targets = plan.Stages
            .SelectMany(stage => stage.Targets)
            .Where(target =>
                string.Equals(target.PackageId, job.ProjectPackageId, StringComparison.Ordinal) &&
                target.PipelineId == job.PipelineId)
            .ToList();
        if (targets is not [var target])
            throw new ValidationException("Kubernetes build target is not unique in its dispatch plan.");
        ProjectLookasideSourcePolicy.Validate(target.LookasideSources);
        return target.LookasideSources ?? [];
    }
}
