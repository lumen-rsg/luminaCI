using Lumina.SecurityService.Services;
using Lumina.Shared.Events;
using MassTransit;

namespace Lumina.SecurityService.Consumers;

public class PackageSigningRequestedConsumer : IConsumer<PackageSigningRequested>
{
    private readonly PgpSigningService _signingService;
    private readonly ILogger<PackageSigningRequestedConsumer> _logger;

    public PackageSigningRequestedConsumer(PgpSigningService signingService, ILogger<PackageSigningRequestedConsumer> logger)
    {
        _signingService = signingService;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PackageSigningRequested> context)
    {
        var msg = context.Message;
        _logger.LogInformation("Received PackageSigningRequested for artifact {ArtifactId}", msg.ArtifactId);

        try
        {
            var signingRequest = await _signingService.SignArtifactAsync(
                msg.ArtifactId, msg.ArtifactPath, msg.ExpectedSha256, msg.KeyId);
            await context.Publish(new PackageSigned(
                msg.ArtifactId,
                signingRequest.KeyFingerprint,
                signingRequest.SignedSha256
                    ?? throw new InvalidOperationException("Signing completed without a signed artifact digest."),
                signingRequest.SignedFileSize
                    ?? throw new InvalidOperationException("Signing completed without a signed artifact size."),
                signingRequest.CompletedAt ?? DateTime.UtcNow));
            _logger.LogInformation("Artifact {ArtifactId} signed successfully", msg.ArtifactId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sign artifact {ArtifactId}", msg.ArtifactId);
            throw;
        }
    }
}
