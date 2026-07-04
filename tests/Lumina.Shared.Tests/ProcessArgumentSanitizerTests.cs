using System.Runtime.InteropServices;
using FsCheck;
using FsCheck.Xunit;
using Lumina.Shared.Extensions;
using Xunit;

namespace Lumina.Shared.Tests;

/// <summary>
/// Tests for <see cref="ProcessArgumentSanitizer"/>. This type is the boundary
/// between untrusted request input and external processes / the filesystem
/// (trivy, gpg, createrepo_c), so a regression here is a security hole, not a
/// bug. Coverage is therefore heavy on the failure modes:
///
/// <list type="bullet">
///  <item><b>Path traversal</b> — explicit known bypasses (absolute paths like
///  <c>/etc/passwd</c> that contain no <c>..</c>, sibling-prefix escapes like
///  <c>/app/builds-evil</c>, encoded traversal) PLUS a property that no
///  traversal-bearing string ever resolves outside the root.</item>
///  <item><b>Allowlist validators</b> — explicit rejection of the GPG
///  batch-script injection (<c>%</c>, <c>:</c>, newlines) and argv-option
///  injection (leading <c>-</c>), plus a property that any control character
///  is rejected everywhere.</item>
/// </list>
/// </summary>
public class ProcessArgumentSanitizerTests
{
    // The confinement logic is OS-agnostic but the literal bypass paths
    // (e.g. "/etc/passwd") are POSIX-specific. Tests that depend on a specific
    // absolute root are skipped on Windows to avoid false negatives from the
    // alternate separator / drive roots.
    private static bool IsPosix => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private const string Root = "/app/builds";
    private const string CanonicalRoot = "/app/builds/";

    // ─── ResolveConfinedPath: legitimate acceptances ─────────────────────

    [Theory]
    [InlineData("foo.rpm", "/app/builds/foo.rpm")]
    [InlineData("el/9/baseos/pkg.rpm", "/app/builds/el/9/baseos/pkg.rpm")]
    [InlineData("./foo.rpm", "/app/builds/foo.rpm")]      // single-dot collapsed by GetFullPath
    public void ResolveConfinedPath_AcceptsRelativeUnderRoot(string input, string expectedSuffix)
    {
        if (!IsPosix) return;
        var resolved = ProcessArgumentSanitizer.ResolveConfinedPath(input, Root);
        Assert.Equal(expectedSuffix, resolved.Replace('\\', '/'));
    }

    [Fact]
    public void ResolveConfinedPath_AcceptsAbsoluteUnderRoot()
    {
        if (!IsPosix) return;
        var resolved = ProcessArgumentSanitizer.ResolveConfinedPath("/app/builds/x/y.rpm", Root);
        Assert.Equal("/app/builds/x/y.rpm", resolved);
    }

    [Fact]
    public void ResolveConfinedPath_IsIdempotent()
    {
        // Resolving an already-resolved path must return the same path — this
        // is what lets callers resolve once and pass the result around safely.
        if (!IsPosix) return;
        var once = ProcessArgumentSanitizer.ResolveConfinedPath("pkg.rpm", Root);
        var twice = ProcessArgumentSanitizer.ResolveConfinedPath(once, Root);
        Assert.Equal(once, twice);
    }

    // ─── ResolveConfinedPath: traversal / absolute-escape rejections ─────
    //
    // These are the regressions that motivated replacing the old ".."-only
    // check with the full-path prefix check. Each is a real bypass class.

    [Theory]
    // Classic traversal.
    [InlineData("../secret.rpm")]
    [InlineData("../../etc/passwd")]
    [InlineData("foo/../../bar.rpm")]
    // The sanitizer intentionally rejects ANY ".." substring, even one that
    // would collapse safely under the root (a/../b). Stricter-by-default is
    // correct for an untrusted boundary — give a clearer error than relying
    // solely on the canonical prefix check.
    [InlineData("a/../b.rpm")]
    // Absolute path that contains NO ".." — the prefix check, not the traversal
    // check, is what must block this. This was the original bug.
    [InlineData("/etc/passwd")]
    [InlineData("/etc/shadow")]
    [InlineData("/root/.ssh/id_rsa")]
    // Sibling-prefix escape: "/app/builds-evil" starts with "/app/builds" as a
    // *string* but is a different directory. The separator boundary must reject.
    [InlineData("/app/builds-evil/x.rpm")]
    [InlineData("/app/buildsevil/x.rpm")]
    // Obfuscated traversal; the sanitizer is not expected to URL-decode, but it
    // must never accept the raw ".." these carry under naïve handling.
    [InlineData("..%2f..%2fetc%2fpasswd")]
    public void ResolveConfinedPath_RejectsTraversalAndAbsoluteEscape(string input)
    {
        if (!IsPosix) return;
        Assert.Throws<ArgumentException>(() =>
            ProcessArgumentSanitizer.ResolveConfinedPath(input, Root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveConfinedPath_RejectsEmptyInput(string? input)
    {
        Assert.Throws<ArgumentException>(() =>
            ProcessArgumentSanitizer.ResolveConfinedPath(input!, Root));
    }

    [Fact]
    public void ResolveConfinedPath_RejectsEmptyRoot()
    {
        Assert.Throws<ArgumentException>(() =>
            ProcessArgumentSanitizer.ResolveConfinedPath("x.rpm", ""));
    }

    [Fact]
    public void ResolveConfinedPath_RejectsControlCharacters()
    {
        // NUL / newline / CR must never reach argv (trivy/gpg/createrepo_c).
        Assert.Throws<ArgumentException>(() =>
            ProcessArgumentSanitizer.ResolveConfinedPath("foo\n.rpm", Root));
        Assert.Throws<ArgumentException>(() =>
            ProcessArgumentSanitizer.ResolveConfinedPath("foo\0.rpm", Root));
        Assert.Throws<ArgumentException>(() =>
            ProcessArgumentSanitizer.ResolveConfinedPath("foo\r.rpm", Root));
    }

    // ─── Property: accepted paths never escape the root ──────────────────
    //
    // For arbitrary input, EITHER it is rejected OR its canonical form lies
    // strictly under the root with a separator boundary. No input may resolve
    // to a path outside /app/builds. This is the dual of the explicit bypass
    // cases above and is what makes the confinement "complete" rather than
    // "tested against the bypasses we thought of".

    [Property(MaxTest = 1000, DisplayName = "ResolveConfinedPath result is always confined or rejected", QuietOnSuccess = true)]
    public Property ResolveConfinedPath_ResultNeverEscapesRoot(string input)
    {
        if (!IsPosix) return true.ToProperty();
        try
        {
            var resolved = ProcessArgumentSanitizer.ResolveConfinedPath(input, Root);
            var norm = resolved.Replace('\\', '/');
            // Must start with the canonical root AND have a separator boundary
            // (so "/app/builds-evil" is not a false positive).
            return norm.StartsWith(CanonicalRoot, StringComparison.OrdinalIgnoreCase)
                .Label($"'{input}' -> '{norm}' escaped root '{CanonicalRoot}'");
        }
        catch (ArgumentException)
        {
            return true.ToProperty();
        }
    }

    // ─── ValidateGitReference: option / ref-injection ────────────────────

    [Theory]
    [InlineData("main")]
    [InlineData("release/1.0")]
    [InlineData("feature/add-login")]
    [InlineData("v1.2.3")]
    [InlineData("bugfix_42")]
    [InlineData("release/1.0+fix")]
    public void ValidateGitReference_AcceptsValidRefs(string reference)
    {
        var result = ProcessArgumentSanitizer.ValidateGitReference(reference);
        Assert.Equal(reference, result);
    }

    [Theory]
    // Option injection: a ref starting with '-' is parsed as a flag by git/gpg.
    [InlineData("-n")]
    [InlineData("--exec=evil")]
    // Path-like escapes.
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("/etc/passwd")]
    [InlineData("./evil")]
    // Git ref-format forbidden sequences.
    [InlineData("a..b")]
    [InlineData("a//b")]
    [InlineData("@{reflog}")]
    [InlineData("feature.lock")]
    // Shell metacharacters / whitespace are outside the allowlist.
    [InlineData("main;rm -rf /")]
    [InlineData("main && evil")]
    [InlineData("main`whoami`")]
    [InlineData("main$(id)")]
    [InlineData("main|nc")]
    [InlineData("main>foo")]
    [InlineData("a b")]
    // Control characters.
    [InlineData("main\n")]
    [InlineData("ma\tin")]
    public void ValidateGitReference_RejectsInjectionPatterns(string reference)
    {
        Assert.Throws<ArgumentException>(() =>
            ProcessArgumentSanitizer.ValidateGitReference(reference));
    }

    // ─── ValidateKeyName: GPG batch-script injection ─────────────────────
    //
    // GPG consumes a batch script where ':' delimits fields and a line starting
    // with '%' is a control directive. A newline lets an attacker start a new
    // directive. Each of these is a real GPG-batch injection vector.

    [Theory]
    [InlineData("Jane Doe")]
    [InlineData("O'Brien")]
    [InlineData("Renée Zawęsowski")]   // accented — old denylist rejected this
    [InlineData("Dev (Ops)")]
    [InlineData("user@host+2")]
    public void ValidateKeyName_AcceptsValidNames(string name)
    {
        var result = ProcessArgumentSanitizer.ValidateKeyName(name);
        Assert.Equal(name, result);
    }

    [Theory]
    [InlineData("Name: evil")]           // GPG field delimiter
    [InlineData("%no-protection")]       // leading control directive
    [InlineData("Name\n%commit")]        // newline + new directive
    [InlineData("Name\r\n%echo hi")]
    [InlineData("Name\0")]
    [InlineData("Name; rm -rf /")]
    [InlineData("Name`id`")]
    [InlineData("Name$(whoami)")]
    [InlineData("Name|evil")]
    public void ValidateKeyName_RejectsInjectionPatterns(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            ProcessArgumentSanitizer.ValidateKeyName(name));
    }

    // ─── ValidateEmail / ValidateGpgPassphrase ───────────────────────────

    [Theory]
    [InlineData("user@example.com")]
    [InlineData("first.last+tag@sub.example.org")]
    // The shared ValidateIdentifier plumbing allows whitespace unconditionally,
    // so a space passes here. This is a known permissiveness for the email
    // field; the GPG field-delimiter (':') and control-char vectors below are
    // the ones that actually matter for batch-script injection.
    [InlineData("a b@example.com")]
    public void ValidateEmail_AcceptsValid(string email)
        => Assert.Equal(email, ProcessArgumentSanitizer.ValidateEmail(email));

    [Theory]
    [InlineData("a\nb@example.com")]     // newline — control char
    [InlineData("a:b@example.com")]      // GPG field delimiter
    [InlineData("user@example.com;rm")]  // ';' outside allowlist
    public void ValidateEmail_RejectsInjectionPatterns(string email)
        => Assert.Throws<ArgumentException>(() => ProcessArgumentSanitizer.ValidateEmail(email));

    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("p@ssw0rd!#%^&*()")]
    [InlineData("ünïcödé-pass")]
    public void ValidateGpgPassphrase_AcceptsValid(string pass)
        => Assert.Equal(pass, ProcessArgumentSanitizer.ValidateGpgPassphrase(pass));

    [Theory]
    [InlineData("%dry-run")]             // leading control directive
    [InlineData("pass\n%commit")]        // newline injection
    [InlineData("pass\0word")]
    [InlineData("pass\r\nextra")]
    public void ValidateGpgPassphrase_RejectsInjectionPatterns(string pass)
        => Assert.Throws<ArgumentException>(() => ProcessArgumentSanitizer.ValidateGpgPassphrase(pass));

    // ─── ValidateRepositoryBasePath / ValidateRepositoryArch ─────────────

    [Theory]
    [InlineData("el/9/baseos")]
    [InlineData("/el/9/baseos")]
    [InlineData("fedora/rawhide")]
    // ValidateRepositoryBasePath is a *segment-shape* gate, not a confinement
    // gate: an absolute path with no ".." passes here by design. The actual
    // filesystem confinement is enforced at the FS boundary by
    // ResolveConfinedPath (see ResolveConfinedPath_RejectsTraversalAndAbsoluteEscape).
    // Including this accepted case makes the defense-in-depth layering explicit
    // and will fail loudly if the segment validator ever starts trying to do
    // confinement itself (which would duplicate and drift from the real check).
    [InlineData("/etc/passwd")]
    public void ValidateRepositoryBasePath_AcceptsValid(string path)
        => Assert.Equal(path, ProcessArgumentSanitizer.ValidateRepositoryBasePath(path));

    [Theory]
    [InlineData("../etc/passwd")]        // ".." segment — explicitly rejected
    [InlineData("el/9/../../etc")]
    [InlineData("-evil")]                // leading option char
    [InlineData(".hidden")]
    [InlineData("el; rm -rf /")]         // ';' outside allowlist
    [InlineData("el\n9")]                // control char
    public void ValidateRepositoryBasePath_RejectsInjectionPatterns(string path)
        => Assert.Throws<ArgumentException>(() =>
            ProcessArgumentSanitizer.ValidateRepositoryBasePath(path));

    [Theory]
    [InlineData("x86_64")]
    [InlineData("aarch64")]
    [InlineData("noarch")]
    public void ValidateRepositoryArch_AcceptsValid(string arch)
        => Assert.Equal(arch, ProcessArgumentSanitizer.ValidateRepositoryArch(arch));

    [Theory]
    [InlineData("-x86")]
    [InlineData("x86 64")]               // space (no separators allowed)
    [InlineData("x86/64")]               // slash
    [InlineData("x86;evil")]
    [InlineData("x86\n64")]
    public void ValidateRepositoryArch_RejectsInjectionPatterns(string arch)
        => Assert.Throws<ArgumentException>(() => ProcessArgumentSanitizer.ValidateRepositoryArch(arch));

    // ─── Property: no validator ever accepts a control character ─────────
    //
    // Control chars (NUL, newline, CR, tab, ESC) are a universal injection
    // substrate across every validator. Rather than enumerate them per
    // validator, this property asserts the invariant holds for any printable
    // string once a control char is spliced into it.

    [Property(MaxTest = 200, DisplayName = "No validator accepts a string containing a control character", QuietOnSuccess = true)]
    public Property NoValidatorAcceptsControlCharacters(NonNull<string> baseValue, char control)
    {
        // Force `control` to actually be a C0/DEL control character regardless
        // of what FsCheck generated, then splice it into a printable base.
        var ctrl = control < 0x20 || control == 0x7f ? control : '\n';
        var seed = new string(baseValue.Get.Where(char.IsLetterOrDigit).ToArray());
        if (seed.Length == 0) seed = "abc";
        var poisoned = seed + ctrl + seed;

        bool allReject =
            Throws<ArgumentException>(() => ProcessArgumentSanitizer.ValidateKeyName(poisoned)) &&
            Throws<ArgumentException>(() => ProcessArgumentSanitizer.ValidateEmail(poisoned)) &&
            Throws<ArgumentException>(() => ProcessArgumentSanitizer.ValidateGpgPassphrase(poisoned)) &&
            Throws<ArgumentException>(() => ProcessArgumentSanitizer.ValidateGitReference(poisoned)) &&
            Throws<ArgumentException>(() => ProcessArgumentSanitizer.ValidateRepositoryArch(poisoned));

        return allReject
            .Label($"Control char U+{(int)ctrl:X4} was accepted by a validator (input='{poisoned}')");
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
