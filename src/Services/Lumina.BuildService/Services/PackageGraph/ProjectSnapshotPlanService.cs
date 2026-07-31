using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumina.BuildService.Data;
using Lumina.Shared.Errors;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services.PackageGraph;

public sealed class ProjectSnapshotPlanService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly BuildDbContext _db;
    private readonly IRepositorySnapshotStreamProvider _snapshots;
    private readonly IProjectLookasideSourceSealer _lookasideSources;

    public ProjectSnapshotPlanService(
        BuildDbContext db,
        IRepositorySnapshotStreamProvider snapshots,
        IProjectLookasideSourceSealer lookasideSources)
    {
        _db = db;
        _snapshots = snapshots;
        _lookasideSources = lookasideSources;
    }

    public async Task ProcessAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var delivery = await _db.ProjectWebhookDeliveries
            .Include(item => item.BuildProject)
            .ThenInclude(project => project.Pipelines)
            .SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (delivery is null)
            return;
        if (delivery.Status is ProjectWebhookStatus.PlanReady or
            ProjectWebhookStatus.Dispatched or ProjectWebhookStatus.Ignored or
            ProjectWebhookStatus.Completed or ProjectWebhookStatus.PromotionPending)
            return;
        try
        {
            if (delivery.Status != ProjectWebhookStatus.SnapshotReady ||
                string.IsNullOrWhiteSpace(delivery.SnapshotStoragePath) ||
                string.IsNullOrWhiteSpace(delivery.SnapshotSha256) ||
                !delivery.SnapshotFileSize.HasValue)
            {
                throw new ValidationException("Webhook delivery does not have a verified repository snapshot.");
            }

            await using var snapshot = await _snapshots.OpenAsync(
                delivery.BuildProjectId,
                delivery.SnapshotStoragePath,
                delivery.SnapshotSha256,
                delivery.SnapshotFileSize.Value,
                cancellationToken);
            var manifest = await RepositorySnapshotArchiveReader.ReadManifestAsync(
                snapshot.Stream,
                delivery.SnapshotFileSize.Value,
                delivery.SnapshotSha256,
                delivery.BuildProject.ManifestPath,
                cancellationToken);
            var plan = ProjectDispatchPlanResolver.Resolve(
                manifest,
                delivery.ChangedPaths,
                delivery.BuildProject.Pipelines);
            await _lookasideSources.SealAsync(plan, cancellationToken);

            delivery.ManifestSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
            delivery.DispatchPlanJson = JsonSerializer.Serialize(plan, JsonOptions);
            delivery.Status = plan.Stages.Count == 0
                ? ProjectWebhookStatus.Ignored
                : ProjectWebhookStatus.PlanReady;
            delivery.FailureCode = null;
            delivery.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (ValidationException)
        {
            delivery.Status = ProjectWebhookStatus.Failed;
            delivery.FailureCode = "snapshot-plan-invalid";
            delivery.DispatchPlanJson = null;
            delivery.ManifestSha256 = null;
            delivery.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
