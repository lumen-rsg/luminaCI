using Lumina.BuildService.Data;
using Lumina.BuildService.Services.PackageGraph;
using Lumina.Shared.Events;
using Lumina.Shared.Models.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Consumers;

public sealed class RepositorySnapshotCompletedConsumer :
    IConsumer<RepositorySnapshotReady>,
    IConsumer<RepositorySnapshotFailed>
{
    private readonly BuildDbContext _db;
    private readonly ProjectSnapshotPlanService _plans;

    public RepositorySnapshotCompletedConsumer(
        BuildDbContext db,
        ProjectSnapshotPlanService plans)
    {
        _db = db;
        _plans = plans;
    }

    public async Task Consume(ConsumeContext<RepositorySnapshotReady> context)
    {
        var message = context.Message;
        var delivery = await _db.ProjectWebhookDeliveries.SingleOrDefaultAsync(
            item => item.Id == message.RequestId,
            context.CancellationToken);
        if (delivery is null)
            return;
        if (delivery.Status is ProjectWebhookStatus.PlanReady or
            ProjectWebhookStatus.Dispatched or ProjectWebhookStatus.Ignored or
            ProjectWebhookStatus.Failed or ProjectWebhookStatus.Completed or
            ProjectWebhookStatus.PromotionPending)
            return;

        if (delivery.BuildProjectId != message.ProjectId ||
            !string.Equals(delivery.CommitSha, message.ResolvedCommit, StringComparison.OrdinalIgnoreCase) ||
            message.HashSha256.Length != 64 || message.HashSha256.Any(character => !Uri.IsHexDigit(character)) ||
            message.FileSize <= 0 || string.IsNullOrWhiteSpace(message.StoragePath))
        {
            delivery.Status = ProjectWebhookStatus.Failed;
            delivery.FailureCode = "snapshot-metadata-mismatch";
        }
        else
        {
            delivery.Status = ProjectWebhookStatus.SnapshotReady;
            delivery.SourceJobId = message.SourceJobId;
            delivery.SnapshotStoragePath = message.StoragePath;
            delivery.SnapshotSha256 = message.HashSha256.ToLowerInvariant();
            delivery.SnapshotFileSize = message.FileSize;
            delivery.FailureCode = null;
        }
        delivery.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(context.CancellationToken);
        if (delivery.Status == ProjectWebhookStatus.SnapshotReady)
            await _plans.ProcessAsync(delivery.Id, context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<RepositorySnapshotFailed> context)
    {
        var message = context.Message;
        var delivery = await _db.ProjectWebhookDeliveries.SingleOrDefaultAsync(
            item => item.Id == message.RequestId,
            context.CancellationToken);
        if (delivery is null)
            return;
        if (delivery.Status is ProjectWebhookStatus.PlanReady or
            ProjectWebhookStatus.Dispatched or ProjectWebhookStatus.Ignored or
            ProjectWebhookStatus.Failed or ProjectWebhookStatus.Completed or
            ProjectWebhookStatus.PromotionPending)
            return;

        delivery.Status = ProjectWebhookStatus.Failed;
        delivery.SourceJobId = message.SourceJobId;
        delivery.FailureCode = message.Reason is "cancelled" ? "snapshot-cancelled" : "snapshot-fetch-failed";
        delivery.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(context.CancellationToken);
    }
}
