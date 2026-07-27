using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.Events;
using Lumina.Shared.Models.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Consumers;

public sealed class PackagePublishFaultConsumer(
    BuildDbContext db,
    PipelineRunCoordinator coordinator) : IConsumer<Fault<PackagePublishRequested>>
{
    public async Task Consume(ConsumeContext<Fault<PackagePublishRequested>> context)
    {
        var artifactId = context.Message.Message.ArtifactId;
        var artifact = await db.BuildArtifacts
            .Where(artifact => artifact.Id == artifactId)
            .Select(artifact => new { artifact.BuildJobId, artifact.PublishedAt })
            .SingleOrDefaultAsync(context.CancellationToken);
        if (artifact is null || artifact.PublishedAt.HasValue)
        {
            return;
        }

        var reason = context.Message.Exceptions.FirstOrDefault()?.Message
            ?? "The repository worker exhausted its retries.";
        await coordinator.FailStepAsync(
            artifact.BuildJobId,
            StepType.Publish,
            reason,
            context.CancellationToken);
    }
}
