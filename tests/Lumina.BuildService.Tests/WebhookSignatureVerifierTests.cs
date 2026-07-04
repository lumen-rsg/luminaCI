using System.Security.Cryptography;
using System.Text;
using FsCheck;
using FsCheck.Xunit;
using Lumina.Shared.Security;
using Xunit;

namespace Lumina.BuildService.Tests;

/// <summary>
/// Tests for <see cref="WebhookSignatureVerifier"/> — the security boundary
/// for an anonymous webhook endpoint. A regression here means an attacker who
/// learns a pipeline id can forge a push and trigger an arbitrary build
/// (chaining into git-clone-as-root and untrusted-spec risks). Coverage:
///
/// <list type="bullet">
///  <item>GitHub/Forgejo HMAC-SHA256: valid signature accepted, every mutation
///  of body / secret / signature rejected.</item>
///  <item>GitLab plaintext token: exact match accepted, any difference rejected.</item>
///  <item>No-header → fail closed.</item>
///  <item>Length-mismatch safety: a truncated/garbled header never throws and
///  never matches.</item>
///  <item>Constant-time property: same-length wrong signatures are all rejected
///  (a non-constant-time compare would early-exit, but we only assert the
///  outcome, not the timing).</item>
/// </list>
/// </summary>
public class WebhookSignatureVerifierTests
{
    private const string Secret = "super-secret-webhook-token";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/main"}""");

    private static string HmacSha256(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(body);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static WebhookHeaders Github(string? sig) => new(null, sig, null);
    private static WebhookHeaders Gitlab(string? token) => new(token, null, null);
    private static WebhookHeaders Forgejo(string? sig) => new(null, null, sig);
    private static WebhookHeaders None => new(null, null, null);

    // ─── GitHub / Forgejo HMAC ───────────────────────────────────────────

    [Fact]
    public void Verify_AcceptsValidGitHubSignature()
    {
        var sig = HmacSha256(Secret, Body);
        Assert.True(WebhookSignatureVerifier.Verify(Secret, Body, Github(sig)));
    }

    [Fact]
    public void Verify_AcceptsValidForgejoSignature()
    {
        var sig = HmacSha256(Secret, Body);
        Assert.True(WebhookSignatureVerifier.Verify(Secret, Body, Forgejo(sig)));
    }

    [Fact]
    public void Verify_AcceptsUppercaseHexSignature()
    {
        // GitHub sends lowercase, but the verifier lower-cases both sides, so an
        // uppercase-hex signature (some proxies/tools normalize differently)
        // must still validate.
        var sig = HmacSha256(Secret, Body).ToUpperInvariant();
        Assert.True(WebhookSignatureVerifier.Verify(Secret, Body, Github(sig)));
    }

    [Fact]
    public void Verify_RejectsTamperedBody()
    {
        // A single byte changed in the body invalidates the HMAC — this is the
        // whole point of signing the raw bytes rather than a re-serialization.
        var sig = HmacSha256(Secret, Body);
        var tampered = Body.Append((byte)0).ToArray();
        Assert.False(WebhookSignatureVerifier.Verify(Secret, tampered, Github(sig)));
    }

    [Fact]
    public void Verify_RejectsWrongSecret()
    {
        var sig = HmacSha256("different-secret", Body);
        Assert.False(WebhookSignatureVerifier.Verify(Secret, Body, Github(sig)));
    }

    [Fact]
    public void Verify_RejectsTamperedSignature()
    {
        var sig = HmacSha256(Secret, Body);
        // Flip one hex char. Same length, different content — must be rejected,
        // and the constant-time compare must not throw on the substitution.
        var tampered = (sig[..^1] + (sig[^1] == '0' ? '1' : '0'));
        Assert.False(WebhookSignatureVerifier.Verify(Secret, Body, Github(tampered)));
    }

    [Theory]
    // Missing "sha256=" prefix.
    [InlineData("abcdef0123456789")]
    // Empty.
    [InlineData("")]
    // Truncated hex.
    [InlineData("sha256=abc")]
    // Wrong algorithm prefix.
    [InlineData("sha1=abcdef")]
    public void Verify_RejectsMalformedGitHubHeader(string header)
        => Assert.False(WebhookSignatureVerifier.Verify(Secret, Body, Github(header)));

    // ─── GitLab token ────────────────────────────────────────────────────

    [Fact]
    public void Verify_AcceptsValidGitLabToken()
        => Assert.True(WebhookSignatureVerifier.Verify(Secret, Body, Gitlab(Secret)));

    [Fact]
    public void Verify_RejectsWrongGitLabToken()
        => Assert.False(WebhookSignatureVerifier.Verify(Secret, Body, Gitlab("wrong-token")));

    [Fact]
    public void Verify_RejectsEmptyGitLabToken()
        => Assert.False(WebhookSignatureVerifier.Verify(Secret, Body, Gitlab("")));

    // ─── Fail-closed / precedence ────────────────────────────────────────

    [Fact]
    public void Verify_FailsClosed_WhenNoHeaderPresent()
        => Assert.False(WebhookSignatureVerifier.Verify(Secret, Body, None));

    [Fact]
    public void Verify_GitLabHeaderTakesPrecedenceOverHmac()
    {
        // If both headers are present, the GitLab token is checked first (the
        // original controller behavior). A valid token validates even when the
        // HMAC header is garbage. Pin this so the extraction didn't silently
        // reorder the checks.
        var headers = new WebhookHeaders(
            GitLabToken: Secret,
            HubSignature256: "garbage",
            ForgejoSignature: null);
        Assert.True(WebhookSignatureVerifier.Verify(Secret, Body, headers));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForNullSecret()
        => Assert.False(WebhookSignatureVerifier.Verify(null!, Body, Gitlab("anything")));

    [Fact]
    public void Verify_ReturnsFalse_ForNullBody()
        => Assert.False(WebhookSignatureVerifier.Verify(Secret, null!, Github("sha256=abc")));

    // ─── Property: HMAC never validates against a body it wasn't computed over ─
    //
    // The dual of the explicit tampered-body test: for any two distinct bodies,
    // a signature computed over one must not validate over the other. This is
    // the collision-resistance property HMAC provides; asserting it here
    // guards against a future "optimization" that compares only a prefix or
    // skips the body entirely.

    [Property(MaxTest = 200, DisplayName = "HMAC signature does not validate over a different body", QuietOnSuccess = true)]
    public Property HmacSignature_DoesNotValidateOverDifferentBody(NonNull<string> a, NonNull<string> b)
    {
        if (a.Get == b.Get) return true.ToProperty();
        var bodyA = Encoding.UTF8.GetBytes(a.Get);
        var bodyB = Encoding.UTF8.GetBytes(b.Get);
        if (bodyA.Length == 0 || bodyB.Length == 0) return true.ToProperty();

        var sigOverA = HmacSha256(Secret, bodyA);
        var validatesOverB = WebhookSignatureVerifier.Verify(Secret, bodyB, Github(sigOverA));
        return (!validatesOverB).Label($"Signature over '{a.Get}' validated over different body '{b.Get}'");
    }

    // ─── Property: any wrong secret is rejected ──────────────────────────

    [Property(MaxTest = 200, DisplayName = "Wrong-secret signature is always rejected", QuietOnSuccess = true)]
    public Property WrongSecretSignature_AlwaysRejected(NonNull<string> wrongSecret)
    {
        if (wrongSecret.Get == Secret) return true.ToProperty();
        var sig = HmacSha256(wrongSecret.Get, Body);
        return (!WebhookSignatureVerifier.Verify(Secret, Body, Github(sig)))
            .Label($"Signature with wrong secret '{wrongSecret.Get}' was accepted");
    }
}
