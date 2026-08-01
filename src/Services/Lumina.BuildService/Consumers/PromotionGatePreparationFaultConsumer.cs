using Lumina.BuildService.Data;
using Lumina.BuildService.Services.PackageGraph;
using Lumina.Shared.Events;
using Lumina.Shared.Models.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Consumers;

public sealed class PromotionGatePreparationFaultConsumer(
    BuildDbContext db,
    ProjectDeliveryFailureService failures)
    : IConsumer<Fault<PromotionGatePreparationRequested>>
{
    public async Task Consume(ConsumeContext<Fault<PromotionGatePreparationRequested>> context)
    {
        var request = context.Message.Message;
        var gate = await db.NativePromotionGates
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
        await failures.FailAsync(
            gate.ProjectWebhookDeliveryId,
            "promotion-gate-preparation-failed",
            context.CancellationToken,
            saveChanges: false);
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
