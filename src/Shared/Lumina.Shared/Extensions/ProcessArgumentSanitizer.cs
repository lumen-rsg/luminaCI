namespace Lumina.Shared.Extensions;

/// <summary>
/// Sanitizes arguments passed to external processes to prevent command injection.
/// </summary>
public static class ProcessArgumentSanitizer
{
    /// <summary>
    /// Escapes a string argument for safe use in process command lines.
    /// Wraps in double quotes and escapes internal quotes/backslashes.
    /// </summary>
    public static string EscapeArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg))
            return "\"\"";

        // Reject obviously malicious patterns
        if (ContainsShellMetacharacters(arg))
            throw new ArgumentException(
                $"Argument contains potentially dangerous shell metacharacters: {arg}",
                nameof(arg));

        // Escape backslashes and quotes, wrap in double quotes
        var escaped = arg.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    /// <summary>
    /// Sanitizes a file path argument — validates it doesn't contain traversal or injection patterns.
    /// </summary>
    public static string SanitizeFilePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("File path cannot be null or empty", nameof(path));

        // Reject path traversal
        var normalizedPath = path.Replace('\\', '/');
        if (normalizedPath.Contains(".."))
            throw new ArgumentException($"File path contains path traversal: {path}", nameof(path));

        // Reject shell metacharacters
        if (ContainsShellMetacharacters(path))
            throw new ArgumentException(
                $"File path contains potentially dangerous characters: {path}",
                nameof(path));

        return EscapeArgument(path);
    }

    /// <summary>
    /// Resolves a client-supplied file path to an absolute path and confines it
    /// underneath <paramref name="rootDirectory"/>. This is the defense against
    /// arbitrary-file-read / path-traversal: callers must never trust a raw
    /// absolute path from a request when opening files with the service account.
    ///
    /// Resolution rules:
    ///  * Relative paths are combined with <paramref name="rootDirectory"/>.
    ///  * Absolute paths are accepted only if they already live under the root
    ///    (after normalization), so legitimate full paths such as
    ///    "/app/builds/.../foo.rpm" keep working.
    ///  * Traversal ("..") is rejected both explicitly and implicitly via the
    ///    canonical full-path prefix check.
    ///  * The returned path is canonicalized with <see cref="Path.GetFullPath"/>
    ///    and verified to start with the canonicalized root (using a directory
    ///    separator boundary so "/app/builds-evil" cannot escape "/app/builds").
    /// </summary>
    /// <param name="filePath">Client-supplied path (absolute or relative to root).</param>
    /// <param name="rootDirectory">Trusted root the final path must stay within.</param>
    /// <returns>The canonicalized absolute path, guaranteed to be under <paramref name="rootDirectory"/>.</returns>
    /// <exception cref="ArgumentException">Thrown if the path is empty, traverses outside the root, or contains shell metacharacters.</exception>
    public static string ResolveConfinedPath(string filePath, string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be null or empty", nameof(filePath));
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Root directory cannot be null or empty", nameof(rootDirectory));

        // Reject shell metacharacters early — confined paths are also frequently
        // forwarded to external processes (trivy, gpg), so the same injection
        // guards as SanitizeFilePath apply.
        if (ContainsShellMetacharacters(filePath))
            throw new ArgumentException(
                $"File path contains potentially dangerous characters: {filePath}", nameof(filePath));

        var normalizedPath = filePath.Replace('\\', '/');
        if (normalizedPath.Contains(".."))
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
        // accepted but "/app/builds-evil/x" is not.
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"File path must stay within {fullRoot}: {filePath}", nameof(filePath));

        return fullPath;
    }

    /// <summary>
    /// Validates a string is safe to embed in a GPG batch script.
    /// Rejects newlines and GPG control directives.
    /// </summary>
    public static string SanitizeGpgField(string value, string fieldName)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException($"{fieldName} cannot be null or empty", fieldName);

        // Reject newlines (GPG script injection)
        if (value.Contains('\n') || value.Contains('\r'))
            throw new ArgumentException($"{fieldName} contains newline characters", fieldName);

        // Reject GPG control directives
        if (value.StartsWith("%", StringComparison.Ordinal))
            throw new ArgumentException($"{fieldName} starts with GPG control character '%'", fieldName);

        // Reject shell metacharacters
        if (ContainsShellMetacharacters(value))
            throw new ArgumentException(
                $"{fieldName} contains potentially dangerous characters: {value}",
                fieldName);

        return value;
    }

    private static bool ContainsShellMetacharacters(string value)
    {
        // Detect shell metacharacters that could lead to injection
        // We check for characters that have special meaning in bash/sh
        var dangerousChars = new[] { '`', '$', ';', '|', '&', '>', '<', '(', ')', '{', '}', '!', '\n', '\r', '\0' };
        foreach (var c in dangerousChars)
        {
            if (value.Contains(c))
                return true;
        }

        // Check for $( command substitution
        if (value.Contains("$(") || value.Contains("${"))
            return true;

        return false;
    }
}