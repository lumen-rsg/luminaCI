using Lumina.RepositoryService.Data;
using Lumina.RepositoryService.Services;
using Lumina.Shared.Errors;
using Lumina.Shared.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.RepositoryService.Consumers;

public sealed class PromotionGateStartedConsumer(
    RepositoryDbContext db,
    ILogger<PromotionGateStartedConsumer> logger) : IConsumer<PromotionGateStarted>
{
    public async Task Consume(ConsumeContext<PromotionGateStarted> context)
    {
        var message = context.Message;
        if (message.PromotionSetId == Guid.Empty || message.RepositoryId == Guid.Empty ||
            message.StartedAt.Kind != DateTimeKind.Utc)
            throw new ValidationException("Native gate start acknowledgement is invalid.");

        var set = await db.PromotionSets
            .Include(item => item.Packages)
            .SingleOrDefaultAsync(item => item.Id == message.PromotionSetId &&
                                          item.RepositoryId == message.RepositoryId,
                context.CancellationToken)
            ?? throw new ValidationException("Native gate promotion set was not found.");
        RepositoryPromotionPolicy.BeginGate(
            set, message.KubernetesJobName, message.KubernetesJobUid, message.StartedAt);
        await db.SaveChangesAsync(context.CancellationToken);
        logger.LogInformation(
            "Recorded native gate Job {JobName} ({JobUid}) for promotion set {PromotionSetId}",
            message.KubernetesJobName, message.KubernetesJobUid, message.PromotionSetId);
    }
}
