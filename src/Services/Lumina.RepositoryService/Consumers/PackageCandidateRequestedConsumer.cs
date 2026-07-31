using Lumina.RepositoryService.Services;
using Lumina.Shared.Events;
using MassTransit;

namespace Lumina.RepositoryService.Consumers;

public sealed class PackageCandidateRequestedConsumer(
    MinioStorageService storage,
    ILogger<PackageCandidateRequestedConsumer> logger)
    : IConsumer<PackageCandidateRequested>
{
    public async Task Consume(ConsumeContext<PackageCandidateRequested> context)
    {
        var request = context.Message;
        var package = await storage.StageCandidateAsync(request);

        await context.Publish(new PackageCandidateStaged(
            request.ArtifactId,
            request.RepositoryId,
            package.Id,
            request.PromotionSetId,
            package.PublishedAt));

        logger.LogInformation(
            "Acknowledged candidate {PackageId} for artifact {ArtifactId} in promotion set {PromotionSetId}",
            package.Id, request.ArtifactId, request.PromotionSetId);
    }
}
