using Lumina.BuildService.Services;
using Lumina.Shared.Events;
using MassTransit;

namespace Lumina.BuildService.Consumers;

public sealed class PackagePublishedConsumer(PipelineRunCoordinator coordinator)
    : IConsumer<PackagePublished>
{
    public Task Consume(ConsumeContext<PackagePublished> context) =>
        coordinator.ReportPublishedAsync(context.Message, context.CancellationToken);
}
