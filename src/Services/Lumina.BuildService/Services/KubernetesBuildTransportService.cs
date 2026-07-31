using Lumina.BuildService.Data;
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
        return await KubernetesBuildTransportPolicy.CreateAsync(
            job,
            delivery,
            identity,
            limits,
            signer,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }
}
