using Lumina.BuildService.Data;
using Lumina.Shared.Events;
using Lumina.Shared.Models.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Consumers;

/// <summary>
/// MassTransit publishes a fault only after the signing consumer exhausts its
/// configured retries. Persist that terminal outcome on the owning build so an
/// RPM cannot remain displayed as a successful-but-unsigned build forever.
/// </summary>
public sealed class PackageSigningFaultConsumer : IConsumer<Fault<PackageSigningRequested>>
{
    private readonly BuildDbContext _db;
    private readonly ILogger<PackageSigningFaultConsumer> _logger;

    public PackageSigningFaultConsumer(BuildDbContext db, ILogger<PackageSigningFaultConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<Fault<PackageSigningRequested>> context)
    {
        var artifactId = context.Message.Message.ArtifactId;
        var job = await _db.BuildJobs
            .Include(b => b.Artifacts)
            .SingleOrDefaultAsync(b => b.Artifacts.Any(a => a.Id == artifactId));

        if (job is null)
        {
            _logger.LogWarning("Signing fault received for unknown artifact {ArtifactId}", artifactId);
            return;
        }

        var artifact = job.Artifacts.Single(a => a.Id == artifactId);
        if (!string.IsNullOrWhiteSpace(artifact.SigningKeyFingerprint))
        {
            _logger.LogInformation(
                "Ignoring stale signing fault for already-signed artifact {ArtifactId}", artifactId);
            return;
        }

        var reason = context.Message.Exceptions.FirstOrDefault()?.Message
            ?? "The signing worker exhausted its retries.";
        job.Status = BuildStatus.Failed;
        job.CompletedAt = DateTime.UtcNow;
        var entry =
            $"[{DateTime.UtcNow:O}] SIGNING FAILED: artifact '{artifact.FileName}' was not published. {reason}";
        job.Logs = string.IsNullOrWhiteSpace(job.Logs) ? entry : $"{job.Logs}\n{entry}";
        await _db.SaveChangesAsync();

        _logger.LogError(
            "Build {BuildJobId} marked failed after signing retries were exhausted for artifact {ArtifactId}",
            job.Id, artifactId);
    }
}
