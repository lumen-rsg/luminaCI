using Lumina.BuildService.Data;
using Lumina.Shared.Events;
using Lumina.Shared.Models.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Consumers;

public sealed class PromotionGatePreparationFaultConsumer(BuildDbContext db)
    : IConsumer<Fault<PromotionGatePreparationRequested>>
{
    public async Task Consume(ConsumeContext<Fault<PromotionGatePreparationRequested>> context)
    {
        var request = context.Message.Message;
        var gate = await db.NativePromotionGates
            .Include(item => item.ProjectWebhookDelivery)
            .SingleOrDefaultAsync(item => item.Id == request.PromotionSetId,
                context.CancellationToken);
        if (gate is null || gate.Status != NativePromotionGateStatus.Pending || gate.BundlePreparedAt is not null)
            return;
        var now = DateTime.UtcNow;
        var reason = context.Message.Exceptions.FirstOrDefault()?.Message
            ?? "Promotion gate bundle preparation exhausted its retries.";
        gate.Status = NativePromotionGateStatus.Failed;
        gate.FailureReason = reason.Length > 2048 ? reason[..2048] : reason;
        gate.CompletedAt = now;
        gate.UpdatedAt = now;
        if (gate.ProjectWebhookDelivery is { Status: ProjectWebhookStatus.PromotionPending } delivery)
        {
            delivery.Status = ProjectWebhookStatus.Failed;
            delivery.FailureCode = "promotion-gate-preparation-failed";
            delivery.UpdatedAt = now;
        }
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
