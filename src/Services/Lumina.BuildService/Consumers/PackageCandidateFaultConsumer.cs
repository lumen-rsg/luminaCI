using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.Events;
using Lumina.Shared.Models.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Consumers;

public sealed class PackageCandidateFaultConsumer(
    BuildDbContext db,
    PipelineRunCoordinator coordinator) : IConsumer<Fault<PackageCandidateRequested>>
{
    public async Task Consume(ConsumeContext<Fault<PackageCandidateRequested>> context)
    {
        var artifactId = context.Message.Message.ArtifactId;
        var artifact = await db.BuildArtifacts
            .Where(artifact => artifact.Id == artifactId)
            .Select(artifact => new { artifact.BuildJobId, artifact.CandidateStagedAt })
            .SingleOrDefaultAsync(context.CancellationToken);
        if (artifact is null || artifact.CandidateStagedAt.HasValue)
            return;

        var reason = context.Message.Exceptions.FirstOrDefault()?.Message
            ?? "The candidate repository worker exhausted its retries.";
        await coordinator.FailStepAsync(
            artifact.BuildJobId,
            StepType.Publish,
            reason,
            context.CancellationToken);
    }
}
