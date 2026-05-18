using Lumina.BuildService.Data;
using Lumina.Shared.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Consumers;

/// <summary>
/// Consumes PackageSigned events from SecurityService.
/// Updates the build artifact with the PGP signature.
/// </summary>
public class PackageSignedConsumer : IConsumer<PackageSigned>
{
    private readonly BuildDbContext _db;
    private readonly ILogger<PackageSignedConsumer> _logger;

    public PackageSignedConsumer(BuildDbContext db, ILogger<PackageSignedConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PackageSigned> context)
    {
        var msg = context.Message;
        _logger.LogInformation("Received PackageSigned for artifact {ArtifactId}", msg.ArtifactId);

        // Find the build job containing this artifact
        var job = await _db.BuildJobs
            .Include(b => b.Artifacts)
            .FirstOrDefaultAsync(b => b.Artifacts.Any(a => a.Id == msg.ArtifactId));

        if (job == null)
        {
            _logger.LogWarning("No build job found for artifact {ArtifactId}", msg.ArtifactId);
            return;
        }

        var artifact = job.Artifacts.First(a => a.Id == msg.ArtifactId);
        artifact.PgpSignature = msg.PgpSignature;
        _db.Update(artifact);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Updated artifact {ArtifactId} with PGP signature", msg.ArtifactId);
    }
}