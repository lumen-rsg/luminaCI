using System.Text.Json;
using Lumina.Shared.Errors;

namespace Lumina.BuildService.Services;

/// <summary>
/// Selects monorepo pipelines from the repository-relative paths carried by a
/// signed push webhook. Filters are literal path prefixes, not shell globs.
/// </summary>
public static class WebhookPathFilter
{
    public static List<string> Normalize(IEnumerable<string>? configured, string? specPath)
    {
        var filters = (configured ?? [])
            .Select(NormalizePath)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (filters.Count == 0 && !string.IsNullOrWhiteSpace(specPath))
        {
            var normalizedSpec = NormalizePath(specPath);
            var separator = normalizedSpec.LastIndexOf('/');
            filters.Add(separator > 0 ? normalizedSpec[..separator] : normalizedSpec);
        }

        foreach (var filter in filters)
        {
            if (filter.Length > 512 || filter.StartsWith('/') ||
                filter.Split('/').Any(segment => segment is "." or "..") ||
                filter.Any(char.IsControl))
            {
                throw new ValidationException(
                    $"Trigger path must be a safe repository-relative prefix: {filter}");
            }
        }

        return filters;
    }

    public static IReadOnlySet<string> ExtractChangedPaths(JsonElement payload)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);

        if (payload.TryGetProperty("commits", out var commits) &&
            commits.ValueKind == JsonValueKind.Array)
        {
            foreach (var commit in commits.EnumerateArray())
                AddCommitPaths(commit, paths);
        }

        if (payload.TryGetProperty("head_commit", out var headCommit) &&
            headCommit.ValueKind == JsonValueKind.Object)
        {
            AddCommitPaths(headCommit, paths);
        }

        return paths;
    }

    public static bool MatchesAny(
        IReadOnlyCollection<string> triggerPaths,
        IReadOnlyCollection<string> changedPaths)
    {
        if (triggerPaths.Count == 0 || changedPaths.Count == 0)
            return true;

        return changedPaths.Any(changed => triggerPaths.Any(filter =>
            string.Equals(changed, filter, StringComparison.Ordinal) ||
            changed.StartsWith(filter + "/", StringComparison.Ordinal)));
    }

    private static void AddCommitPaths(JsonElement commit, ISet<string> paths)
    {
        foreach (var propertyName in new[] { "added", "modified", "removed" })
        {
            if (!commit.TryGetProperty(propertyName, out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    continue;
                var normalized = NormalizePath(entry.GetString());
                if (normalized.Length > 0)
                    paths.Add(normalized);
            }
        }
    }

    private static string NormalizePath(string? path) =>
        (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
}
