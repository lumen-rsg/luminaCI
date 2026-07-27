using Lumina.SecurityService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lumina.SecurityService.Health;

public sealed class SigningReadinessHealthCheck(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SecurityDbContext>();
        var activeKey = await db.SecurityKeys
            .AsNoTracking()
            .Where(key => key.IsActive)
            .Select(key => key.KeyId)
            .SingleOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(activeKey))
        {
            return HealthCheckResult.Unhealthy("No active RPM signing key is configured.");
        }

        var keyDirectory = configuration["Gpg:KeyDirectory"] ?? "/app/keys";
        if (!Directory.Exists(keyDirectory))
        {
            return HealthCheckResult.Unhealthy($"Signing key directory '{keyDirectory}' does not exist.");
        }

        var passphraseFile = configuration["Gpg:PassphraseFile"];
        if (string.IsNullOrWhiteSpace(passphraseFile) || !File.Exists(passphraseFile))
        {
            return HealthCheckResult.Unhealthy("The signing passphrase secret is unavailable.");
        }

        return HealthCheckResult.Healthy(
            "An active signing key and signing secret are available.",
            new Dictionary<string, object> { ["keyId"] = activeKey });
    }
}
