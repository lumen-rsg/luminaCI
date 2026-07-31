using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.Errors;
using Lumina.Shared.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Consumers;

public sealed class PromotionGatePreparedConsumer(BuildDbContext db)
    : IConsumer<PromotionGatePrepared>
{
    public async Task Consume(ConsumeContext<PromotionGatePrepared> context)
    {
        var gate = await db.NativePromotionGates.SingleOrDefaultAsync(
            item => item.Id == context.Message.PromotionSetId,
            context.CancellationToken)
            ?? throw new ValidationException("Prepared native promotion gate was not found.");
        NativePromotionGateManifestPolicy.RecordPrepared(gate, context.Message);
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
