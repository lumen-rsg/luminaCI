using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Consumers;

/// <summary>
/// Consumes PackageSigned events from SecurityService.
/// Records metadata only after SecurityService embedded and verified the RPM
/// signature, and replaces the pre-sign hash/size with the final file values.
/// </summary>
public class PackageSignedConsumer : IConsumer<PackageSigned>
{
    private readonly BuildDbContext _db;
    private readonly ArtifactStorageService _storage;
    private readonly ILogger<PackageSignedConsumer> _logger;

    public PackageSignedConsumer(
        BuildDbContext db,
        ArtifactStorageService storage,
        ILogger<PackageSignedConsumer> logger)
    {
        _db = db;
        _storage = storage;
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
        var storagePath = await _storage.UploadSignedArtifactAsync(
            artifact.FilePath,
            artifact.FileName,
            msg.SignedSha256,
            msg.SignedFileSize,
            context.CancellationToken);

        artifact.SigningKeyFingerprint = msg.KeyFingerprint;
        artifact.SignedAt = msg.SignedAt;
        artifact.HashSha256 = msg.SignedSha256;
        artifact.FileSize = msg.SignedFileSize;
        artifact.StoragePath = storagePath;
        _db.Update(artifact);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Updated artifact {ArtifactId} with verified RPM signature metadata for {Fingerprint}",
            msg.ArtifactId, msg.KeyFingerprint);
    }
}
