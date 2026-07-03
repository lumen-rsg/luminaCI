using Lumina.BuildService.Data;
using Lumina.Shared.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Consumers;

/// <summary>
/// Answers <see cref="GetArtifactSignature"/> requests over the message bus so
/// RepositoryService can enforce the "no unsigned publication" gate at publish
/// time without a direct view of BuildDbContext. Returns the stored
/// <see cref="BuildArtifact.PgpSignature"/> (null when the artifact was never
/// signed); RepositoryService treats null/empty as "publish blocked".
/// </summary>
public class GetArtifactSignatureConsumer : IConsumer<GetArtifactSignature>
{
    private readonly BuildDbContext _db;
    private readonly ILogger<GetArtifactSignatureConsumer> _logger;

    public GetArtifactSignatureConsumer(BuildDbContext db, ILogger<GetArtifactSignatureConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<GetArtifactSignature> context)
    {
        var artifactId = context.Message.ArtifactId;
        var signature = await _db.BuildArtifacts
            .Where(a => a.Id == artifactId)
            .Select(a => a.PgpSignature)
            .FirstOrDefaultAsync();

        if (signature is null)
        {
            _logger.LogWarning("GetArtifactSignature for {ArtifactId}: artifact has no stored PGP signature", artifactId);
        }

        await context.RespondAsync(new ArtifactSignature(signature));
    }
}
