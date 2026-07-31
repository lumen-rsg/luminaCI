using Lumina.RepositoryService.Services;
using Lumina.Shared.Events;
using MassTransit;

namespace Lumina.RepositoryService.Consumers;

public sealed class PromotionGatePreparationRequestedConsumer(
    PromotionGateBundleService bundles,
    ILogger<PromotionGatePreparationRequestedConsumer> logger)
    : IConsumer<PromotionGatePreparationRequested>
{
    public async Task Consume(ConsumeContext<PromotionGatePreparationRequested> context)
    {
        var prepared = await bundles.PrepareAsync(context.Message, context.CancellationToken);
        await context.Publish(prepared, context.CancellationToken);
        logger.LogInformation(
            "Acknowledged immutable gate bundle for promotion set {PromotionSetId}",
            prepared.PromotionSetId);
    }
}
