using Lumina.BuildService.Services;
using Lumina.Shared.Events;
using MassTransit;

namespace Lumina.BuildService.Consumers;

public class CveScanCompletedConsumer : IConsumer<CveScanCompleted>
{
    private readonly PipelineRunCoordinator _coordinator;
    private readonly ILogger<CveScanCompletedConsumer> _logger;

    public CveScanCompletedConsumer(
        PipelineRunCoordinator coordinator,
        ILogger<CveScanCompletedConsumer> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<CveScanCompleted> context)
    {
        var msg = context.Message;
        _logger.LogInformation("Received CveScanCompleted for artifact {ArtifactId}: Status={Status}, Critical={Critical}, High={High}",
            msg.ArtifactId, msg.Status, msg.CriticalCount, msg.HighCount);

        await _coordinator.ReportScanAsync(msg, context.CancellationToken);
    }
}
