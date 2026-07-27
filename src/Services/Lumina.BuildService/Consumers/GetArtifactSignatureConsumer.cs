using Lumina.BuildService.Data;
using Lumina.Shared.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Consumers;

/// <summary>
/// Answers <see cref="GetArtifactSigningMetadata"/> requests over the message bus so
/// RepositoryService can enforce the "no unsigned publication" gate at publish
/// time without a direct view of BuildDbContext. Returns the stored
/// the publisher can fail closed unless SecurityService recorded a verified
/// embedded RPM signature and final signed digest.
/// </summary>
public class GetArtifactSignatureConsumer : IConsumer<GetArtifactSigningMetadata>
{
    private readonly BuildDbContext _db;
    private readonly ILogger<GetArtifactSignatureConsumer> _logger;

    public GetArtifactSignatureConsumer(BuildDbContext db, ILogger<GetArtifactSignatureConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<GetArtifactSigningMetadata> context)
    {
        var artifactId = context.Message.ArtifactId;
        var signing = await _db.BuildArtifacts
            .Where(a => a.Id == artifactId)
            .Select(a => new { a.SigningKeyFingerprint, a.HashSha256, a.SignedAt })
            .FirstOrDefaultAsync();

        if (signing?.SigningKeyFingerprint is null)
        {
            _logger.LogWarning("Artifact {ArtifactId} has no verified embedded RPM signature", artifactId);
        }

        await context.RespondAsync(new ArtifactSigningMetadata(
            signing?.SigningKeyFingerprint, signing?.HashSha256, signing?.SignedAt));
    }
}
