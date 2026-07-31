using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.Errors;
using Lumina.Shared.Events;
using Lumina.Shared.Models.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Consumers;

public sealed class PromotionSetPublishedConsumer(
    BuildDbContext db,
    PipelineRunCoordinator coordinator) : IConsumer<PromotionSetPublished>
{
    public async Task Consume(ConsumeContext<PromotionSetPublished> context)
    {
        var message = context.Message;
        if (message.PromotionSetId == Guid.Empty || message.RepositoryId == Guid.Empty ||
            message.PublishedAt.Kind != DateTimeKind.Utc || message.ArtifactIds is not { Count: > 0 } ||
            message.ArtifactIds.Any(item => item == Guid.Empty) ||
            message.ArtifactIds.Distinct().Count() != message.ArtifactIds.Count)
            throw new ValidationException("Promotion publication acknowledgement is invalid.");
        var gate = await db.NativePromotionGates.Include(item => item.ProjectWebhookDelivery)
            .SingleOrDefaultAsync(item => item.Id == message.PromotionSetId,
                context.CancellationToken)
            ?? throw new ValidationException("Published native promotion gate was not found.");
        var expected = NativePromotionGateManifestPolicy.Read(gate).Candidates
            .Select(item => item.ArtifactId).Order().ToList();
        if (gate.RepositoryId != message.RepositoryId || gate.Status != NativePromotionGateStatus.Passed ||
            !expected.SequenceEqual(message.ArtifactIds.Order()))
            throw new ValidationException("Promotion publication provenance does not match the native gate.");

        foreach (var artifactId in expected)
            await coordinator.ReportPublishedAsync(
                new PackagePublished(artifactId, message.RepositoryId, Guid.Empty, message.PublishedAt),
                context.CancellationToken);

        var delivery = gate.ProjectWebhookDelivery
            ?? throw new ValidationException("Promotion delivery was not found.");
        if (delivery.Status == ProjectWebhookStatus.Completed)
            return;
        if (delivery.Status != ProjectWebhookStatus.PromotionPending)
            throw new ConflictException("Promotion delivery is not awaiting publication.");
        delivery.Status = ProjectWebhookStatus.Completed;
        delivery.FailureCode = null;
        delivery.UpdatedAt = message.PublishedAt;
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
