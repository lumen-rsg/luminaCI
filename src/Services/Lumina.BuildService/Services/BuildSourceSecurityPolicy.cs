namespace Lumina.BuildService.Services;

/// <summary>
/// Keeps source credentials outside the untrusted RPM runner. Credentialed
/// fetches must be resolved by a trusted source service into a mounted,
/// credential-free snapshot before the build container is launched.
/// </summary>
public static class BuildSourceSecurityPolicy
{
    public static void EnsureCredentialFree(
        string? sourceUrl,
        string? gitUsername,
        string? gitToken)
    {
        if (!string.IsNullOrWhiteSpace(gitUsername) ||
            !string.IsNullOrWhiteSpace(gitToken))
        {
            throw new InvalidOperationException(
                "Credentialed source fetches must be resolved by the trusted source service before launching an RPM build container.");
        }

        var candidate = sourceUrl;
        if (candidate?.StartsWith("git://", StringComparison.Ordinal) == true)
            candidate = candidate["git://".Length..].Split('#', 2)[0];

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                "Source URLs containing credentials are forbidden in RPM build containers.");
        }
    }
}
