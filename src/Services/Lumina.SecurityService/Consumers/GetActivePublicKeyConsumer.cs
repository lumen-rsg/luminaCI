using Lumina.SecurityService.Services;
using Lumina.Shared.Events;
using MassTransit;

namespace Lumina.SecurityService.Consumers;

/// <summary>
/// Answers <see cref="GetActivePublicKey"/> requests over the message bus so
/// RepositoryService can verify externally-uploaded RPM signatures with
/// <c>gpg --verify</c>. RepositoryService runs in its own container with its
/// own transient keyring and shares no filesystem with SecurityService, so it
/// fetches the active armored public key here. <c>PublicKeyArmored</c> is null
/// when no active key exists.
/// </summary>
public class GetActivePublicKeyConsumer : IConsumer<GetActivePublicKey>
{
    private readonly PgpSigningService _pgp;
    private readonly ILogger<GetActivePublicKeyConsumer> _logger;

    public GetActivePublicKeyConsumer(PgpSigningService pgp, ILogger<GetActivePublicKeyConsumer> logger)
    {
        _pgp = pgp;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<GetActivePublicKey> context)
    {
        var active = await _pgp.GetActiveKeyAsync();

        if (active is null)
        {
            _logger.LogWarning("GetActivePublicKey: no active PGP key — uploads cannot be verified until one is generated");
        }

        await context.RespondAsync(new ActivePublicKey(active?.PublicKey));
    }
}
