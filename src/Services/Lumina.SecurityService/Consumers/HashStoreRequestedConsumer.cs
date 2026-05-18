using Lumina.SecurityService.Services;
using Lumina.Shared.Events;
using MassTransit;

namespace Lumina.SecurityService.Consumers;

/// <summary>
/// Consumes HashStoreRequested events from BuildService.
/// Stores pre-computed hashes for build artifacts.
/// </summary>
public class HashStoreRequestedConsumer : IConsumer<HashStoreRequested>
{
    private readonly HashService _hashService;
    private readonly ILogger<HashStoreRequestedConsumer> _logger;

    public HashStoreRequestedConsumer(HashService hashService, ILogger<HashStoreRequestedConsumer> logger)
    {
        _hashService = hashService;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<HashStoreRequested> context)
    {
        var msg = context.Message;
        _logger.LogInformation("Received HashStoreRequested for artifact {ArtifactId}: {FileName}",
            msg.ArtifactId, msg.FileName);

        try
        {
            await _hashService.StorePrecomputedHashAsync(
                msg.ArtifactId,
                msg.FileName,
                msg.Sha256,
                msg.Md5,
                msg.FileSize);

            _logger.LogInformation("Hash stored for artifact {ArtifactId}", msg.ArtifactId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store hash for artifact {ArtifactId}", msg.ArtifactId);
            throw; // Re-throw so MassTransit retries
        }
    }
}