using System.Security.Cryptography;
using System.Text;

namespace Lumina.Shared.Security;

/// <summary>
/// The subset of webhook request headers that participate in authentication.
/// Deliberately a plain struct with no ASP.NET dependency so the verifier can
/// live in <see cref="Lumina.Shared"/> and be unit-tested without a web host.
/// Callers map their <c>IHeaderDictionary</c> onto this; any header absent on
/// the wire is passed as <c>null</c>.
/// </summary>
public readonly record struct WebhookHeaders(
    string? GitLabToken,
    string? HubSignature256,
    string? ForgejoSignature);

/// <summary>
/// Pure, side-effect-free verification of a webhook's authentication header
/// against a pipeline's shared secret. Extracted from
/// <c>WebhooksController.VerifySignature</c> so the HMAC / constant-time token
/// comparison — the security boundary for an anonymous endpoint — is covered by
/// unit tests instead of living in an untestable controller private.
///
/// <para><b>Behavior contract</b> (must match the providers exactly):</para>
/// <list type="bullet">
///  <item><b>GitLab</b> sends the secret verbatim in <c>X-Gitlab-Token</c>; we
///  compare it byte-for-byte in constant time.</item>
///  <item><b>GitHub / Forgejo / Gitea</b> send <c>sha256=&lt;hex&gt;</c> in
///  <c>X-Hub-Signature-256</c> / <c>X-Forgejo-Signature</c>; the HMAC is
///  computed over the raw request bytes and compared in constant time against
///  the supplied header (case-insensitively, after lower-casing both
///  sides).</item>
///  <item>If no recognized header is present, the request is rejected
///  (<c>false</c>) — there is no "no-auth" path.</item>
/// </list>
///
/// <para>All comparisons go through <see cref="CryptographicOperations.FixedTimeEquals"/>,
/// which returns <c>false</c> (never throws) on a length mismatch. That is the
/// property that makes a timing-safe comparison possible even though the
/// attacker-controlled header may be any length.</para>
/// </summary>
public static class WebhookSignatureVerifier
{
    /// <summary>
    /// Verifies the webhook authentication header present in
    /// <paramref name="headers"/> against <paramref name="secret"/> over
    /// <paramref name="rawBody"/>.
    /// </summary>
    /// <param name="secret">The pipeline's configured webhook secret (never null).</param>
    /// <param name="rawBody">The exact request bytes the provider signed (never null).</param>
    /// <param name="headers">The relevant auth headers from the request.</param>
    /// <returns><c>true</c> iff a recognized header is present and matches.</returns>
    public static bool Verify(string secret, byte[] rawBody, in WebhookHeaders headers)
    {
        if (secret is null) return false;
        if (rawBody is null) return false;

        // GitLab: plaintext shared-secret token. Constant-time compare the UTF-8
        // bytes so an attacker cannot learn the secret byte-by-byte via timing.
        if (headers.GitLabToken is not null)
        {
            var tokenBytes = Encoding.UTF8.GetBytes(headers.GitLabToken);
            var secretBytes = Encoding.UTF8.GetBytes(secret);
            return FixedTimeEquals(tokenBytes, secretBytes);
        }

        // GitHub / Forgejo / Gitea: HMAC-SHA256 over the raw body. The provider
        // sends "sha256=<hex>"; we recompute over exactly the bytes it signed.
        var signatureHeader = headers.HubSignature256 ?? headers.ForgejoSignature;
        if (signatureHeader is not null)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(rawBody);
            var computed = "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();

            // Lower-case both sides before comparing: GitHub sends lowercase hex,
            // but accept either to avoid a false rejection over casing alone.
            return FixedTimeEquals(
                Encoding.UTF8.GetBytes(computed.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(signatureHeader.ToLowerInvariant()));
        }

        // No recognized auth header — fail closed.
        return false;
    }

    /// <summary>
    /// Length-safe wrapper around <see cref="CryptographicOperations.FixedTimeEquals"/>.
    /// The BCL overload already returns <c>false</c> on a length mismatch (it
    /// does not throw), but going through this helper makes that guarantee an
    /// explicit, named part of the contract rather than something callers must
    /// remember about the framework.
    /// </summary>
    private static bool FixedTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
