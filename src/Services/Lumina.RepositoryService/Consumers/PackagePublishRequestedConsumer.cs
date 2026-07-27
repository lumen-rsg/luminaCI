using Lumina.RepositoryService.Services;
using Lumina.Shared.Events;
using MassTransit;

namespace Lumina.RepositoryService.Consumers;

public sealed class PackagePublishRequestedConsumer(
    MinioStorageService storage,
    ILogger<PackagePublishRequestedConsumer> logger)
    : IConsumer<PackagePublishRequested>
{
    public async Task Consume(ConsumeContext<PackagePublishRequested> context)
    {
        var request = context.Message;
        var package = await storage.PublishPackageAsync(
            request.ArtifactId,
            request.RepositoryId,
            request.PublishedBy);

        await context.Publish(new PackagePublished(
            request.ArtifactId,
            request.RepositoryId,
            package.Id,
            package.PublishedAt));

        logger.LogInformation(
            "Published artifact {ArtifactId} to repository {RepositoryId} from pipeline execution",
            request.ArtifactId, request.RepositoryId);
    }
}
