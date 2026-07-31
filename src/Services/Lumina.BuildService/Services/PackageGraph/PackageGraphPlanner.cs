using System.Text.RegularExpressions;
using Lumina.Shared.Errors;

namespace Lumina.BuildService.Services.PackageGraph;

/// <summary>
/// Validates a repository package graph and creates deterministic build stages
/// for the paths changed by a push. The planner has no executor dependency, so
/// the same result can drive Docker and Kubernetes builds.
/// </summary>
public static partial class PackageGraphPlanner
{
    public const string ManifestPath = ".lumina/packages.yaml";

    public static PackageBuildPlan Plan(
        RepositoryPackageGraph graph,
        IReadOnlyCollection<string> changedPaths,
        IReadOnlySet<string> supportedTargets)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(changedPaths);
        ArgumentNullException.ThrowIfNull(supportedTargets);

        var packages = ValidateAndNormalize(graph, supportedTargets);
        var conservative = changedPaths.Count == 0 || changedPaths
            .Select(NormalizePath)
            .Any(path => string.Equals(path, ManifestPath, StringComparison.Ordinal));

        var reasons = packages.Keys.ToDictionary(
            id => id,
            _ => new SortedSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        if (conservative)
        {
            var reason = changedPaths.Count == 0
                ? "changed paths unavailable"
                : $"manifest changed: {ManifestPath}";
            foreach (var packageId in packages.Keys)
                reasons[packageId].Add(reason);
        }
        else
        {
            foreach (var changedPath in changedPaths.Select(NormalizePath).Distinct(StringComparer.Ordinal))
            {
                ValidateRepositoryPath(changedPath, "Changed path");
                foreach (var package in packages.Values)
                {
                    if (package.Paths.Any(pattern => MatchesPath(pattern, changedPath)))
                        reasons[package.Id].Add($"path changed: {changedPath}");
                }
            }
        }

        AddReverseDependents(packages, reasons);

        var selectedIds = reasons
            .Where(pair => pair.Value.Count > 0)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        var stages = CreateStages(packages, selectedIds);

        return new PackageBuildPlan(
            stages,
            selectedIds
                .Order(StringComparer.Ordinal)
                .Select(id => new PackageSelection(id, reasons[id].ToList()))
                .ToList(),
            packages.Keys.Except(selectedIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            conservative);
    }

    private static SortedDictionary<string, NormalizedPackage> ValidateAndNormalize(
        RepositoryPackageGraph graph,
        IReadOnlySet<string> supportedTargets)
    {
        if (graph.Version != 1)
            throw new ValidationException("Package graph version must be 1.");
        if (graph.Packages is null || graph.Packages.Count == 0)
            throw new ValidationException("Package graph must declare at least one package.");
        if (supportedTargets.Count == 0)
            throw new ValidationException("At least one administrator-supported target is required.");

        var packages = new SortedDictionary<string, NormalizedPackage>(StringComparer.Ordinal);
        var specOwners = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var definition in graph.Packages)
        {
            var id = definition.Id?.Trim() ?? string.Empty;
            if (!PackageIdPattern().IsMatch(id))
                throw new ValidationException($"Package ID is invalid: {id}");
            if (packages.ContainsKey(id))
                throw new ValidationException($"Package ID is declared more than once: {id}");

            var specPath = NormalizePath(definition.SpecPath);
            ValidateRepositoryPath(specPath, $"Spec path for package '{id}'");
            if (!specPath.EndsWith(".spec", StringComparison.Ordinal))
                throw new ValidationException($"Spec path for package '{id}' must end in .spec.");
            if (specOwners.TryGetValue(specPath, out var existingOwner))
            {
                throw new ValidationException(
                    $"Spec path '{specPath}' is owned by both '{existingOwner}' and '{id}'.");
            }
            specOwners.Add(specPath, id);

            if (definition.Paths is null || definition.Paths.Count == 0)
                throw new ValidationException($"Package '{id}' must declare at least one input path.");
            var paths = definition.Paths.Select(path => NormalizePattern(path, id))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();

            if (definition.Targets is null || definition.Targets.Count == 0)
                throw new ValidationException($"Package '{id}' must declare at least one target.");
            var targets = definition.Targets
                .Select(target => target?.Trim().ToLowerInvariant() ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            var unknownTarget = targets.FirstOrDefault(target => !supportedTargets.Contains(target));
            if (unknownTarget is not null)
                throw new ValidationException($"Package '{id}' uses unsupported target '{unknownTarget}'.");

            var dependencies = (definition.DependsOn ?? [])
                .Select(dependency => dependency?.Trim() ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (dependencies.Any(dependency => !PackageIdPattern().IsMatch(dependency)))
                throw new ValidationException($"Package '{id}' contains an invalid dependency ID.");
            if (dependencies.Contains(id, StringComparer.Ordinal))
                throw new ValidationException($"Package '{id}' cannot depend on itself.");

            packages.Add(id, new NormalizedPackage(
                id,
                specPath,
                paths,
                targets,
                dependencies,
                definition.PromotionGroup?.Trim(),
                definition.RebuildOnDependencyChange));
        }

        foreach (var package in packages.Values)
        {
            var missing = package.DependsOn.FirstOrDefault(dependency => !packages.ContainsKey(dependency));
            if (missing is not null)
                throw new ValidationException($"Package '{package.Id}' depends on unknown package '{missing}'.");
        }

        EnsureAcyclic(packages);
        return packages;
    }

    private static void EnsureAcyclic(IReadOnlyDictionary<string, NormalizedPackage> packages)
    {
        var state = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var path = new List<string>();

        foreach (var packageId in packages.Keys)
            Visit(packageId);
        return;

        void Visit(string packageId)
        {
            if (state.TryGetValue(packageId, out var current))
            {
                if (current == VisitState.Complete)
                    return;
                if (current == VisitState.Visiting)
                {
                    var cycleStart = path.IndexOf(packageId);
                    var cycle = path.Skip(cycleStart).Append(packageId);
                    throw new ValidationException($"Package dependency cycle detected: {string.Join(" -> ", cycle)}");
                }
            }

            state[packageId] = VisitState.Visiting;
            path.Add(packageId);
            foreach (var dependency in packages[packageId].DependsOn)
                Visit(dependency);
            path.RemoveAt(path.Count - 1);
            state[packageId] = VisitState.Complete;
        }
    }

    private static void AddReverseDependents(
        IReadOnlyDictionary<string, NormalizedPackage> packages,
        IDictionary<string, SortedSet<string>> reasons)
    {
        var queue = new Queue<string>(reasons
            .Where(pair => pair.Value.Count > 0)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal));
        var expanded = new HashSet<string>(StringComparer.Ordinal);

        while (queue.TryDequeue(out var changedPackageId))
        {
            if (!expanded.Add(changedPackageId))
                continue;

            foreach (var dependent in packages.Values
                         .Where(package => package.RebuildOnDependencyChange &&
                                           package.DependsOn.Contains(changedPackageId, StringComparer.Ordinal))
                         .OrderBy(package => package.Id, StringComparer.Ordinal))
            {
                if (reasons[dependent.Id].Add($"dependency selected: {changedPackageId}"))
                    queue.Enqueue(dependent.Id);
            }
        }
    }

    private static List<PackageBuildStage> CreateStages(
        IReadOnlyDictionary<string, NormalizedPackage> packages,
        IReadOnlySet<string> selectedIds)
    {
        var remaining = selectedIds.ToHashSet(StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var stages = new List<PackageBuildStage>();

        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(id => packages[id].DependsOn.All(dependency =>
                    !selectedIds.Contains(dependency) || completed.Contains(dependency)))
                .Order(StringComparer.Ordinal)
                .ToList();
            if (ready.Count == 0)
                throw new ValidationException("Selected package graph cannot be ordered.");

            stages.Add(new PackageBuildStage(stages.Count, ready));
            foreach (var packageId in ready)
            {
                remaining.Remove(packageId);
                completed.Add(packageId);
            }
        }

        return stages;
    }

    private static string NormalizePattern(string? pattern, string packageId)
    {
        var normalized = NormalizePath(pattern);
        var path = normalized.EndsWith("/**", StringComparison.Ordinal)
            ? normalized[..^3]
            : normalized;
        ValidateRepositoryPath(path, $"Input path for package '{packageId}'");
        if (normalized.Contains('*') && !normalized.EndsWith("/**", StringComparison.Ordinal))
        {
            throw new ValidationException(
                $"Input path for package '{packageId}' may only use a trailing '/**' wildcard: {normalized}");
        }
        if (normalized.Contains('?') || normalized.Contains('[') || normalized.Contains(']'))
            throw new ValidationException($"Input path for package '{packageId}' contains an unsupported wildcard.");
        return normalized;
    }

    private static bool MatchesPath(string pattern, string changedPath)
    {
        var prefix = pattern.EndsWith("/**", StringComparison.Ordinal)
            ? pattern[..^3]
            : pattern;
        return string.Equals(prefix, changedPath, StringComparison.Ordinal) ||
               changedPath.StartsWith(prefix + "/", StringComparison.Ordinal);
    }

    private static void ValidateRepositoryPath(string path, string label)
    {
        if (path.Length == 0 || path.Length > 512 || path.StartsWith('/') ||
            path.Split('/').Any(segment => segment is "" or "." or "..") ||
            path.Any(char.IsControl))
        {
            throw new ValidationException($"{label} must be a safe repository-relative path: {path}");
        }
    }

    private static string NormalizePath(string? path) =>
        (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');

    [GeneratedRegex("^[a-z0-9][a-z0-9+_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdPattern();

    private enum VisitState
    {
        Visiting,
        Complete
    }

    private sealed record NormalizedPackage(
        string Id,
        string SpecPath,
        IReadOnlyList<string> Paths,
        IReadOnlyList<string> Targets,
        IReadOnlyList<string> DependsOn,
        string? PromotionGroup,
        bool RebuildOnDependencyChange);
}
