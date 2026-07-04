using Lumina.Shared.Errors;

namespace Lumina.BuildService.Services;

/// <summary>
/// Fail-closed gate that confirms an active PGP signing key exists before a
/// Sign-enabled pipeline may build. Implemented by
/// <see cref="SigningKeyGate"/>, which asks SecurityService over the message
/// bus. Exists as a test seam so <see cref="PipelineEngine"/> can be unit-
/// tested without RabbitMQ: the engine only needs to know that a Sign step was
/// rejected (it does not care how the key was discovered).
/// </summary>
public interface ISigningKeyGate
{
    /// <summary>
    /// Returns normally if an active signing key is confirmed; throws
    /// <see cref="ValidationException"/> (fail-closed) if no key is active or
    /// the lookup itself failed.
    /// </summary>
    Task RequireActiveKeyAsync(CancellationToken cancellationToken = default);
}
