using Lumina.RepositoryService.Data;
using Lumina.RepositoryService.Services;
using Lumina.Shared.Events;
using Lumina.Shared.Models.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.RepositoryService.Consumers;

public sealed class PromotionGatePreparationFaultConsumer(RepositoryDbContext db)
    : IConsumer<Fault<PromotionGatePreparationRequested>>
{
    public async Task Consume(ConsumeContext<Fault<PromotionGatePreparationRequested>> context)
    {
        var set = await db.PromotionSets.SingleOrDefaultAsync(
            item => item.Id == context.Message.Message.PromotionSetId,
            context.CancellationToken);
        if (set is null || set.Status is not (PromotionSetStatus.Candidate or PromotionSetStatus.Failed) ||
            set.GateBundlePreparedAt is not null)
            return;
        var reason = context.Message.Exceptions.FirstOrDefault()?.Message
            ?? "Promotion gate bundle preparation exhausted its retries.";
        RepositoryPromotionPolicy.RecordGatePreparationFailure(set, reason, DateTime.UtcNow);
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
