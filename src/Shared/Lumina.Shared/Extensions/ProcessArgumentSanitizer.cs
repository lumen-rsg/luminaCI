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