using Lumina.RepositoryService.Data;
using Lumina.RepositoryService.Services;
using Lumina.Shared.Errors;
using Lumina.Shared.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.RepositoryService.Consumers;

public sealed class PromotionGateCompletedConsumer(
    RepositoryDbContext db,
    ILogger<PromotionGateCompletedConsumer> logger) : IConsumer<PromotionGateCompleted>
{
    public async Task Consume(ConsumeContext<PromotionGateCompleted> context)
    {
        var message = context.Message;
        if (message.PromotionSetId == Guid.Empty || message.RepositoryId == Guid.Empty ||
            message.CompletedAt.Kind != DateTimeKind.Utc ||
            (message.Succeeded
                ? message.ResultSha256 is null || message.FailureReason is not null
                : message.ResultSha256 is not null || string.IsNullOrWhiteSpace(message.FailureReason)))
            throw new ValidationException("Native gate result acknowledgement is invalid.");

        var set = await db.PromotionSets
            .Include(item => item.Packages)
            .SingleOrDefaultAsync(item => item.Id == message.PromotionSetId &&
                                          item.RepositoryId == message.RepositoryId,
                context.CancellationToken)
            ?? throw new ValidationException("Native gate promotion set was not found.");
        if (!string.Equals(set.GateJobName, message.KubernetesJobName, StringComparison.Ordinal) ||
            !string.Equals(set.GateJobUid, message.KubernetesJobUid, StringComparison.Ordinal) ||
            !string.Equals(set.GateRunnerImageDigest, message.RunnerImageDigest, StringComparison.Ordinal))
            throw new ValidationException("Native gate result provenance does not match the promotion set.");

        if (message.Succeeded)
            RepositoryPromotionPolicy.RecordGateSuccess(set, message.ResultSha256!, message.CompletedAt);
        else
            RepositoryPromotionPolicy.RecordGateFailure(set, message.FailureReason!, message.CompletedAt);
        await db.SaveChangesAsync(context.CancellationToken);
        logger.LogInformation(
            "Recorded native gate {Outcome} for promotion set {PromotionSetId}",
            message.Succeeded ? "success" : "failure", message.PromotionSetId);
    }
}
