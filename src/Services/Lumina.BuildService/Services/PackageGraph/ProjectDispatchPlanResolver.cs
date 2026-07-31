using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;

namespace Lumina.BuildService.Services.PackageGraph;

public sealed record ProjectDispatchPlan(
    IReadOnlyList<ProjectDispatchStage> Stages,
    IReadOnlyList<PackageSelection> Selected,
    IReadOnlyList<string> Skipped,
    bool IsConservative);

public sealed record ProjectDispatchStage(
    int Order,
    IReadOnlyList<ProjectDispatchTarget> Targets);

public sealed record ProjectDispatchTarget(
    string PackageId,
    Guid PipelineId,
    string BuildProfile,
    string SpecPath);

public static class ProjectDispatchPlanResolver
{
    private static readonly IReadOnlySet<string> SupportedTargets =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "fedora-44-aarch64",
            "fedora-44-x86_64"
        };

    public static ProjectDispatchPlan Resolve(
        string manifestYaml,
        IReadOnlyCollection<string> changedPaths,
        IReadOnlyCollection<Pipeline> boundPipelines)
    {
        var graph = PackageGraphManifestLoader.Load(manifestYaml);
        var packageDefinitions = graph.Packages.ToDictionary(
            package => package.Id.Trim(), StringComparer.Ordinal);
        var bindings = ValidateBindings(packageDefinitions, boundPipelines);
        var packagePlan = PackageGraphPlanner.Plan(graph, changedPaths, SupportedTargets);

        var stages = packagePlan.Stages.Select(stage => new ProjectDispatchStage(
            stage.Order,
            stage.PackageIds.Select(packageId =>
            {
                var pipeline = bindings[packageId];
                return new ProjectDispatchTarget(
                    packageId,
                    pipeline.Id,
                    pipeline.BuildProfile,
                    NormalizePath(pipeline.SpecPath!));
            }).ToList())).ToList();

        return new ProjectDispatchPlan(
            stages,
            packagePlan.Selected,
            packagePlan.Skipped,
            packagePlan.IsConservative);
    }

    private static IReadOnlyDictionary<string, Pipeline> ValidateBindings(
        IReadOnlyDictionary<string, RepositoryPackageDefinition> packages,
        IReadOnlyCollection<Pipeline> boundPipelines)
    {
        if (boundPipelines.Select(pipeline => pipeline.BuildProjectId).Distinct().Count() > 1 ||
            boundPipelines.Any(pipeline => !pipeline.BuildProjectId.HasValue))
        {
            throw new ValidationException("Pipeline bindings must belong to one build project.");
        }
        var bindings = new Dictionary<string, Pipeline>(StringComparer.Ordinal);
        foreach (var pipeline in boundPipelines)
        {
            var packageId = pipeline.PackageId?.Trim();
            if (string.IsNullOrWhiteSpace(packageId))
                throw new ValidationException($"Bound pipeline {pipeline.Id} has no package ID.");
            if (!packages.ContainsKey(packageId))
                throw new ValidationException($"Pipeline binding references unknown package '{packageId}'.");
            if (!bindings.TryAdd(packageId, pipeline))
                throw new ValidationException($"Package '{packageId}' has more than one pipeline binding.");
        }

        foreach (var (packageId, definition) in packages)
        {
            if (!bindings.TryGetValue(packageId, out var pipeline))
                throw new ValidationException($"Package '{packageId}' has no pipeline binding.");
            if (pipeline.Status != PipelineStatus.Active)
                throw new ValidationException($"Pipeline for package '{packageId}' is not active.");
            if (!string.IsNullOrWhiteSpace(pipeline.GitUsername) ||
                !string.IsNullOrWhiteSpace(pipeline.GitToken))
            {
                throw new ValidationException(
                    $"Pipeline for package '{packageId}' has credentials that cannot enter a build runner.");
            }
            if (definition.Targets.Count != 1 ||
                !string.Equals(definition.Targets[0].Trim(), pipeline.BuildProfile, StringComparison.Ordinal))
            {
                throw new ValidationException(
                    $"Pipeline target for package '{packageId}' does not match its manifest target.");
            }
            if (string.IsNullOrWhiteSpace(pipeline.SpecPath) ||
                !string.Equals(
                    NormalizePath(definition.SpecPath),
                    NormalizePath(pipeline.SpecPath),
                    StringComparison.Ordinal))
            {
                throw new ValidationException(
                    $"Pipeline spec path for package '{packageId}' does not match the manifest.");
            }
        }

        return bindings;
    }

    private static string NormalizePath(string path) =>
        path.Trim().Replace('\\', '/').TrimEnd('/');
}
