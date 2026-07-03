using System.Globalization;

namespace Lumina.Shared.Extensions;

/// <summary>
/// Helpers that harden the boundary between untrusted user input
/// (<c>conf.ini</c> values, request bodies) and the filesystem / external
/// processes.
///
/// <para><b>Design note.</b> Earlier versions of this type used a denylist of
/// "shell metacharacters" (<c>ContainsShellMetacharacters</c>) plus a
/// <c>SanitizeFilePath</c> that only rejected literal "..". Both were weak:
/// the denylist rejected legitimate identifiers (parentheses, accented names,
/// braces) while still being bypassable with novel encodings, and the path
/// check let absolute paths such as <c>/etc/passwd</c> through because they
/// contain no traversal segment.</para>
///
/// <para>The current API replaces that with two complementary primitives:</para>
/// <list type="bullet">
///  <item><see cref="ResolveConfinedPath"/> — canonicalizes a path with
///  <see cref="Path.GetFullPath"/> and verifies it stays under a trusted root.
///  This is the path-traversal / arbitrary-file-read defense and is the only
///  path check callers should use.</item>
///  <item>A family of allowlist validators (<see cref="ValidateKeyName"/>,
///  <see cref="ValidateEmail"/>, <see cref="ValidateGpgPassphrase"/>,
///  <see cref="ValidateGitReference"/>) — each defines the exact character set
///  its input may use, so there is no denylist to bypass and no legitimate
///  value to silently reject.</item>
/// </list>
/// </summary>
public static class ProcessArgumentSanitizer
{
    /// <summary>
    /// Resolves a client-supplied file path to an absolute path and confines it
    /// underneath <paramref name="rootDirectory"/>. This is the defense against
    /// arbitrary-file-read / path-traversal: callers must never trust a raw
    /// absolute path from a request when opening files with the service account.
    ///
    /// <para>Resolution rules:</para>
    /// <list type="bullet">
    ///  <item>Relative paths are combined with <paramref name="rootDirectory"/>.</item>
    ///  <item>Absolute paths are accepted only if they already live under the root
    ///   (after normalization), so legitimate full paths such as
    ///   "/app/builds/.../foo.rpm" keep working but "/etc/passwd" is rejected.</item>
    ///  <item>Traversal ("..") is rejected both explicitly and implicitly via the
    ///   canonical full-path prefix check — the prefix check is what stops
    ///   "/etc/passwd", which contains no "..".</item>
    ///  <item>The returned path is canonicalized with <see cref="Path.GetFullPath"/>
    ///   and verified to start with the canonicalized root (using a directory
    ///   separator boundary so "/app/builds-evil" cannot escape "/app/builds").</item>
    /// </list>
    /// </summary>
    /// <param name="filePath">Client-supplied path (absolute or relative to root).</param>
    /// <param name="rootDirectory">Trusted root the final path must stay within.</param>
    /// <returns>The canonicalized absolute path, guaranteed to be under <paramref name="rootDirectory"/>.</returns>
    /// <exception cref="ArgumentException">Thrown if the path is empty, traverses outside the root, or contains control characters.</exception>
    public static string ResolveConfinedPath(string filePath, string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be null or empty", nameof(filePath));
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Root directory cannot be null or empty", nameof(rootDirectory));

        // Reject control characters outright — confined paths are also forwarded
        // to external processes (trivy, gpg, createrepo_c), and embedded NULs /
        // newlines must never reach argv.
        AssertNoControlCharacters(filePath, nameof(filePath));

        // An explicit ".." segment check gives a clearer error than the prefix
        // comparison alone; the prefix check below is still the real boundary.
        var normalizedForTraversal = filePath.Replace('\\', '/');
        if (normalizedForTraversal.Contains(".."))
            throw new ArgumentException($"File path contains path traversal: {filePath}", nameof(filePath));

        // Canonicalize the root first so the prefix comparison is robust against
        // trailing separators / symlink-free lexical differences.
        var fullRoot = Path.GetFullPath(rootDirectory);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            fullRoot += Path.DirectorySeparatorChar;

        // Anchor the input: relative paths are joined to the root; absolute paths
        // that already live under the root pass through, others are rejected.
        string combined;
        if (Path.IsPathRooted(filePath))
            combined = filePath;
        else
            combined = Path.Combine(fullRoot, filePath);

        var fullPath = Path.GetFullPath(combined);

        // Prefix check with a separator boundary: ensures "/app/builds/x" is
        // accepted but "/app/builds-evil/x" is not. This — not the ".." check —
        // is what blocks absolute paths like "/etc/passwd".
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"File path must stay within {fullRoot}: {filePath}", nameof(filePath));

        return fullPath;
    }

    // ─── Allowlist validators ─────────────────────────────────────────────
    //
    // Each validator defines the *exact* character set its input may contain.
    // This is deliberately stricter than the old denylist: there is nothing to
    // bypass and nothing legitimate to silently reject.

    /// <summary>
    /// Validates a GPG key "Name-Real" value. Allows letters (incl. accented),
    /// digits, spaces, and a small set of common punctuation. Rejects newlines
    /// (GPG batch-script injection), NULs, the leading "%" control directive,
    /// and the ":" used to delimit GPG batch fields.
    /// </summary>
    public static string ValidateKeyName(string value)
    {
        const string Allowed = " .,()'-@+/_";
        return ValidateIdentifier(value, nameof(value), allowExtra: Allowed,
            rejectColon: true, rejectLeadingPercent: true, maxLength: 120);
    }

    /// <summary>
    /// Validates a GPG "Name-Email" value. Allows the email subset
    /// (letters/digits and . _ % + - @) and rejects anything else.
    /// </summary>
    public static string ValidateEmail(string value)
    {
        const string Allowed = "._%+-@";
        return ValidateIdentifier(value, nameof(value), allowExtra: Allowed,
            rejectColon: true, rejectLeadingPercent: false, maxLength: 254);
    }

    /// <summary>
    /// Validates a GPG passphrase. Passphrases may contain almost any printable
    /// Unicode (including spaces and most punctuation), but must not contain
    /// newlines/NUL (which would break the batch script) or a leading "%"
    /// (GPG control directive).
    /// </summary>
    public static string ValidateGpgPassphrase(string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("passphrase cannot be null or empty", "passphrase");

        // No control characters at all — this covers \n, \r, \0 and tabs.
        AssertNoControlCharacters(value, "passphrase");

        // GPG batch scripts treat a line starting with '%' as a control directive.
        if (value.StartsWith("%", StringComparison.Ordinal))
            throw new ArgumentException("passphrase starts with GPG control character '%'", "passphrase");

        if (value.Length > 4096)
            throw new ArgumentException("passphrase exceeds maximum length", "passphrase");

        return value;
    }

    /// <summary>
    /// Validates a VCS branch / tag / reference name (git, hg, svn).
    /// Permits Unicode letters, digits, and the limited punctuation that
    /// appears in real refs (<c>.</c>, <c>-</c>, <c>_</c>, <c>/</c>, <c>+</c>).
    /// Rejects refs that start with "-", "/", or "." (option/escape hazards)
    /// and anything outside the allowlist — so ref values are always passed to
    /// tools as discrete argv tokens and never re-parsed as options.
    /// </summary>
    public static string ValidateGitReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new ArgumentException("reference cannot be null or empty", nameof(reference));

        AssertNoControlCharacters(reference, nameof(reference));

        // git does not allow these anyway; rejecting them also prevents the
        // value from being interpreted as an option by downstream tools.
        if (reference is "." or "..")
            throw new ArgumentException($"Invalid git reference: '{reference}'", nameof(reference));
        if (reference[0] is '-' or '/' or '.')
            throw new ArgumentException(
                $"Reference must not start with '-', '/', or '.': {reference}", nameof(reference));
        if (reference.EndsWith(".lock", StringComparison.Ordinal))
            throw new ArgumentException($"Reference must not end with '.lock': {reference}", nameof(reference));
        if (reference.Contains("..") || reference.Contains("//") || reference.Contains("@{"))
            throw new ArgumentException($"Reference contains a forbidden sequence: {reference}", nameof(reference));

        const string Allowed = "./-+_";
        foreach (var c in reference)
        {
            // Letters (incl. accented) and digits: allowed everywhere.
            if (char.IsLetterOrDigit(c)) continue;
            if (Allowed.Contains(c)) continue;
            throw new ArgumentException(
                $"Reference contains forbidden character '{c}': {reference}", nameof(reference));
        }

        if (reference.Length > 200)
            throw new ArgumentException("Reference exceeds maximum length", nameof(reference));

        return reference;
    }

    // ─── Shared validation plumbing ───────────────────────────────────────

    private static string ValidateIdentifier(
        string value, string paramName, string allowExtra,
        bool rejectColon, bool rejectLeadingPercent, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException($"{paramName} cannot be null or empty", paramName);

        AssertNoControlCharacters(value, paramName);

        if (value.StartsWith("%", StringComparison.Ordinal) && rejectLeadingPercent)
            throw new ArgumentException($"{paramName} starts with GPG control character '%'", paramName);

        foreach (var c in value)
        {
            // Letters (incl. accented Unicode) and digits are always allowed —
            // this is the key difference from the old denylist, which rejected
            // "(" and accented names outright.
            if (char.IsLetterOrDigit(c)) continue;
            if (char.IsWhiteSpace(c)) continue;
            if (allowExtra.Contains(c)) continue;

            if (rejectColon && c == ':')
                throw new ArgumentException(
                    $"{paramName} contains forbidden character ':' (GPG field delimiter): {value}", paramName);

            throw new ArgumentException(
                $"{paramName} contains forbidden character '{c}': {value}", paramName);
        }

        if (value.Length > maxLength)
            throw new ArgumentException($"{paramName} exceeds maximum length of {maxLength}", paramName);

        return value;
    }

    private static void AssertNoControlCharacters(string value, string paramName)
    {
        // Reject C0 control characters (incl. NUL, \n, \r, \t). Use a manual
        // scan so we can report the offending character; StringInfo is overkill
        // here because control chars are always BMP code units.
        foreach (var c in value)
        {
            if (char.IsControl(c))
                throw new ArgumentException(
                    $"{paramName} contains control character U+{((int)c).ToString("X4", CultureInfo.InvariantCulture)}",
                    paramName);
        }
    }
}
