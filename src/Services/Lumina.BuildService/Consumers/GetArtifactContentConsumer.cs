using Lumina.BuildService.Data;
using Lumina.Shared.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Consumers;

/// <summary>
/// Answers <see cref="GetArtifactContent"/> requests over the message bus so
/// RepositoryService can publish a built RPM without a shared filesystem or a
/// direct view of BuildDbContext. Reads the artifact bytes from
/// <see cref="BuildArtifact.FilePath"/> (the same on-disk path served by
/// <c>BuildsController.DownloadArtifact</c>) and returns them together with the
/// real NEVRA <see cref="BuildArtifact.FileName"/> and the hash/size already
/// computed at scan time.
/// <para>If the artifact row or its file is missing, the request faults (throws)
/// rather than returning empty content — RepositoryService treats that as a
/// publish failure (fail-closed), mirroring <see cref="GetArtifactSignatureConsumer"/>.
/// </para></summary>
public class GetArtifactContentConsumer : IConsumer<GetArtifactContent>
{
    private readonly BuildDbContext _db;
    private readonly ILogger<GetArtifactContentConsumer> _logger;

    public GetArtifactContentConsumer(BuildDbContext db, ILogger<GetArtifactContentConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<GetArtifactContent> context)
    {
        var artifactId = context.Message.ArtifactId;

        var artifact = await _db.BuildArtifacts
            .Where(a => a.Id == artifactId)
            .Select(a => new { a.FileName, a.FilePath, a.HashSha256, a.FileSize })
            .FirstOrDefaultAsync();

        if (artifact is null)
        {
            _logger.LogError("GetArtifactContent for {ArtifactId}: artifact not found", artifactId);
            throw new InvalidOperationException($"Build artifact {artifactId} not found");
        }

        if (string.IsNullOrEmpty(artifact.FilePath) || !File.Exists(artifact.FilePath))
        {
            _logger.LogError("GetArtifactContent for {ArtifactId}: file missing on disk ({Path})", artifactId, artifact.FilePath);
            throw new InvalidOperationException($"Build artifact {artifactId} file not found on disk");
        }

        var content = await File.ReadAllBytesAsync(artifact.FilePath);

        await context.RespondAsync(new ArtifactContent(artifact.FileName, artifact.FileSize, artifact.HashSha256, content));
    }
}
