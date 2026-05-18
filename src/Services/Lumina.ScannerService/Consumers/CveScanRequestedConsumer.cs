using Lumina.ScannerService.Services;
using Lumina.Shared.Events;
using Lumina.Shared.Models.Enums;
using MassTransit;

namespace Lumina.ScannerService.Consumers;

public class CveScanRequestedConsumer : IConsumer<CveScanRequested>
{
    private readonly TrivyScannerService _scannerService;
    private readonly ILogger<CveScanRequestedConsumer> _logger;

    public CveScanRequestedConsumer(TrivyScannerService scannerService, ILogger<CveScanRequestedConsumer> logger)
    {
        _scannerService = scannerService;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<CveScanRequested> context)
    {
        var msg = context.Message;
        _logger.LogInformation("Received CveScanRequested for artifact {ArtifactId}: {FileName}",
            msg.ArtifactId, msg.FileName);

        try
        {
            var report = await _scannerService.ScanArtifactAsync(msg.ArtifactId, msg.ArtifactPath, msg.FileName);

            await context.Publish(new CveScanCompleted(
                msg.ArtifactId, report.Status, report.CriticalCount,
                report.HighCount, report.MediumCount, report.LowCount, DateTime.UtcNow));

            _logger.LogInformation("CVE scan completed for {FileName}: {Critical} critical, {High} high",
                msg.FileName, report.CriticalCount, report.HighCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CVE scan failed for artifact {ArtifactId}", msg.ArtifactId);
            await context.Publish(new CveScanCompleted(
                msg.ArtifactId, ScanStatus.Failed, 0, 0, 0, 0, DateTime.UtcNow));
        }
    }
}
