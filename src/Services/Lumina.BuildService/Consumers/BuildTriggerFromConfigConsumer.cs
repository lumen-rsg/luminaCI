using Lumina.BuildService.Services;
using Lumina.Shared.Events;
using MassTransit;

namespace Lumina.BuildService.Consumers;

/// <summary>
/// Consumes BuildTriggerFromConfig events from SourceService.
/// Triggers a build using pre-fetched sources from conf.ini configuration.
/// </summary>
public class BuildTriggerFromConfigConsumer : IConsumer<BuildTriggerFromConfig>
{
    private readonly PipelineEngine _pipelineEngine;
    private readonly ILogger<BuildTriggerFromConfigConsumer> _logger;

    public BuildTriggerFromConfigConsumer(PipelineEngine pipelineEngine, ILogger<BuildTriggerFromConfigConsumer> logger)
    {
        _pipelineEngine = pipelineEngine;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<BuildTriggerFromConfig> context)
    {
        var msg = context.Message;
        _logger.LogInformation("Received BuildTriggerFromConfig for package {PackageName} from {SourceDir}",
            msg.PackageName, msg.SourceDir);

        try
        {
            await _pipelineEngine.TriggerBuildFromConfigAsync(
                msg.PackageName,
                msg.SourceDir,
                msg.SpecContent,
                msg.SpecName,
                msg.BuildImage,
                msg.TriggeredBy);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger build for package {PackageName}", msg.PackageName);
            throw; // Re-throw so MassTransit retries
        }
    }
}