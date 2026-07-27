using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Consumers;

/// <summary>
/// Returns the immutable object-store reference for a signed RPM. The message
/// remains small regardless of package size; RepositoryService streams the
/// object directly from MinIO and verifies its digest before publication.
/// </summary>
public class GetArtifactLocationConsumer : IConsumer<GetArtifactLocation>
{
    private readonly BuildDbContext _db;
    private readonly ILogger<GetArtifactLocationConsumer> _logger;

    public GetArtifactLocationConsumer(BuildDbContext db, ILogger<GetArtifactLocationConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<GetArtifactLocation> context)
    {
        var artifactId = context.Message.ArtifactId;

        var artifact = await _db.BuildArtifacts
            .Where(a => a.Id == artifactId)
            .Select(a => new
            {
                a.FileName,
                a.StoragePath,
                a.HashSha256,
                a.FileSize,
                a.SigningKeyFingerprint,
                a.SignedAt
            })
            .FirstOrDefaultAsync();

        if (artifact is null)
        {
            _logger.LogError("GetArtifactLocation for {ArtifactId}: artifact not found", artifactId);
            throw new InvalidOperationException($"Build artifact {artifactId} not found");
        }

        if (string.IsNullOrWhiteSpace(artifact.StoragePath) ||
            string.IsNullOrWhiteSpace(artifact.HashSha256) ||
            string.IsNullOrWhiteSpace(artifact.SigningKeyFingerprint) ||
            artifact.SignedAt is null)
        {
            _logger.LogWarning(
                "GetArtifactLocation for {ArtifactId}: final signed object is unavailable",
                artifactId);
            throw new InvalidOperationException(
                $"Build artifact {artifactId} has no final signed object");
        }

        await context.RespondAsync(new ArtifactLocation(
            artifact.FileName,
            ArtifactStorageService.BucketName,
            artifact.StoragePath,
            artifact.FileSize,
            artifact.HashSha256));
    }
}
