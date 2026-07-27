using Lumina.SecurityService.Services;
using Lumina.Shared.Events;
using MassTransit;

namespace Lumina.SecurityService.Consumers;

/// <summary>
/// Answers <see cref="GetActiveSigningKey"/> requests over the message bus so
/// BuildService never has to call SecurityService's HTTP API directly. The
/// previous direct <c>GET /api/security/keys</c> call carried no JWT and would
/// be rejected now that SecurityController is gated by <c>[Authorize]</c>.
/// </summary>
public class GetActiveSigningKeyConsumer : IConsumer<GetActiveSigningKey>
{
    private readonly PgpSigningService _pgp;
    private readonly ILogger<GetActiveSigningKeyConsumer> _logger;

    public GetActiveSigningKeyConsumer(PgpSigningService pgp, ILogger<GetActiveSigningKeyConsumer> logger)
    {
        _pgp = pgp;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<GetActiveSigningKey> context)
    {
        var active = await _pgp.GetActiveKeyAsync();

        if (active is null)
        {
            _logger.LogWarning("No active PGP key found — signing will be skipped until one is generated");
        }

        await context.RespondAsync(new ActiveSigningKey(active?.Id));
    }
}
