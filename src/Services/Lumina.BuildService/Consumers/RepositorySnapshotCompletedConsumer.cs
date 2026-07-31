using Lumina.BuildService.Data;
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

    public RepositorySnapshotCompletedConsumer(BuildDbContext db)
    {
        _db = db;
    }

    public async Task Consume(ConsumeContext<RepositorySnapshotReady> context)
    {
        var message = context.Message;
        var delivery = await _db.ProjectWebhookDeliveries.SingleOrDefaultAsync(
            item => item.Id == message.RequestId,
            context.CancellationToken);
        if (delivery is null)
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
            delivery.FailureCode = null;
        }
        delivery.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<RepositorySnapshotFailed> context)
    {
        var message = context.Message;
        var delivery = await _db.ProjectWebhookDeliveries.SingleOrDefaultAsync(
            item => item.Id == message.RequestId,
            context.CancellationToken);
        if (delivery is null)
            return;

        delivery.Status = ProjectWebhookStatus.Failed;
        delivery.SourceJobId = message.SourceJobId;
        delivery.FailureCode = message.Reason is "cancelled" ? "snapshot-cancelled" : "snapshot-fetch-failed";
        delivery.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(context.CancellationToken);
    }
}
