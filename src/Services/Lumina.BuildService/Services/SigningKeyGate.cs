using Lumina.Shared.Errors;
using Lumina.Shared.Events;
using MassTransit;

namespace Lumina.BuildService.Services;

/// <summary>
/// Production implementation of <see cref="ISigningKeyGate"/>. Confirms an
/// active PGP key exists by asking SecurityService over the MassTransit bus
/// (no HTTP, no token surface). Fail-closed: a missing key, an unreachable
/// SecurityService, or a bus fault all surface as a
/// <see cref="ValidationException"/> so a Sign-enabled build can never start
/// without assurance the artifact can be signed.
/// </summary>
public class SigningKeyGate : ISigningKeyGate
{
    /// <summary>
    /// Operator-facing message shown when a Sign step is requested but no key
    /// is active. Centralized so the gate and the test doubles reference the
    /// same string.
    /// </summary>
    public const string NoActiveKeyMessage =
        "No active PGP key. Generate a key in Security settings before triggering a pipeline that includes a Sign step — unsigned artifacts cannot be published.";

    public const string SecurityServiceUnreachableMessage =
        "Could not confirm an active PGP signing key (SecurityService unreachable). Cannot start a Sign-enabled build without assurance that the artifact can be signed.";

    private readonly IBus _bus;
    private readonly ILogger<SigningKeyGate> _logger;

    public SigningKeyGate(IBus bus, ILogger<SigningKeyGate> logger)
    {
        _bus = bus;
        _logger = logger;
    }

    public async Task RequireActiveKeyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _bus.Request<GetActiveSigningKey, ActiveSigningKey>(
                new GetActiveSigningKey(), cancellationToken, TimeSpan.FromSeconds(10));

            if (!response.Message.KeyId.HasValue)
            {
                throw new ValidationException(NoActiveKeyMessage);
            }
        }
        catch (ValidationException)
        {
            throw; // our own gate message — propagate verbatim
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to confirm an active PGP key exists via the SecurityService bus; rejecting build trigger (fail-closed)");
            throw new ValidationException(SecurityServiceUnreachableMessage);
        }
    }
}
