using Lumina.BuildService.Services;
using Lumina.Shared.Events;
using MassTransit;

namespace Lumina.BuildService.Consumers;

public sealed class PackageCandidateStagedConsumer(PipelineRunCoordinator coordinator)
    : IConsumer<PackageCandidateStaged>
{
    public Task Consume(ConsumeContext<PackageCandidateStaged> context) =>
        coordinator.ReportCandidateStagedAsync(context.Message, context.CancellationToken);
}
