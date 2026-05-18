using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.Events;
using Lumina.Shared.Models.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Consumers;

public class CveScanCompletedConsumer : IConsumer<CveScanCompleted>
{
    private readonly BuildDbContext _db;
    private readonly PipelineEngine _pipelineEngine;
    private readonly ILogger<CveScanCompletedConsumer> _logger;

    public CveScanCompletedConsumer(BuildDbContext db, PipelineEngine pipelineEngine, ILogger<CveScanCompletedConsumer> logger)
    {
        _db = db;
        _pipelineEngine = pipelineEngine;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<CveScanCompleted> context)
    {
        var msg = context.Message;
        _logger.LogInformation("Received CveScanCompleted for artifact {ArtifactId}: Status={Status}", msg.ArtifactId, msg.Status);

        var job = await _db.BuildJobs.Include(b => b.Artifacts)
            .FirstOrDefaultAsync(b => b.Artifacts.Any(a => a.Id == msg.ArtifactId));

        if (job == null) { _logger.LogWarning("No build job found for artifact {ArtifactId}", msg.ArtifactId); return; }

        var artifact = job.Artifacts.First(a => a.Id == msg.ArtifactId);
        artifact.CveScanStatus = msg.Status;
        _db.Update(artifact);

        if (msg.Status == ScanStatus.Completed && msg.CriticalCount == 0 && msg.HighCount == 0)
        {
            await context.Publish(new PackageSigningRequested(msg.ArtifactId, artifact.FilePath ?? "", artifact.FileName, Guid.Empty, DateTime.UtcNow));
        }

        await _db.SaveChangesAsync();
    }
}
