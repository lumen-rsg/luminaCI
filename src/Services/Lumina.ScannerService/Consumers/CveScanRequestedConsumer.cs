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
            var report = await _scannerService.ScanArtifactAsync(
                msg.ArtifactId,
                msg.ArtifactPath,
                msg.ScannerType,
                msg.ExpectedSha256,
                msg.ExpectedFileSize);

            await context.Publish(new CveScanCompleted(
                msg.ArtifactId, report.ArtifactSha256 ?? msg.ExpectedSha256, report.Status, report.CriticalCount,
                report.HighCount, report.MediumCount, report.LowCount, report.UnknownCount, DateTime.UtcNow));

            _logger.LogInformation("CVE scan completed for {FileName}: status={Status}, {Critical} critical, {High} high, {Unknown} unknown",
                msg.FileName, report.Status, report.CriticalCount, report.HighCount, report.UnknownCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CVE scan failed for artifact {ArtifactId}", msg.ArtifactId);
            // Fail-closed: all counts zero is intentional here because we never
            // parsed anything. Status=Failed drives the signing gate to block.
            await context.Publish(new CveScanCompleted(
                msg.ArtifactId, msg.ExpectedSha256, ScanStatus.Failed, 0, 0, 0, 0, 0, DateTime.UtcNow));
        }
    }
}
