namespace Lumina.BuildService.Services.PackageGraph;

public sealed record RepositoryPackageGraph(
    int Version,
    IReadOnlyList<RepositoryPackageDefinition> Packages);

public sealed record RepositoryPackageDefinition(
    string Id,
    string SpecPath,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Targets,
    IReadOnlyList<string>? DependsOn = null,
    string? PromotionGroup = null,
    bool RebuildOnDependencyChange = true,
    IReadOnlyList<RepositoryLookasideSource>? LookasideSources = null);

public sealed record RepositoryLookasideSource(
    string FileName,
    long Size,
    string Sha256);

public sealed record PackageBuildPlan(
    IReadOnlyList<PackageBuildStage> Stages,
    IReadOnlyList<PackageSelection> Selected,
    IReadOnlyList<string> Skipped,
    bool IsConservative);

public sealed record PackageBuildStage(
    int Order,
    IReadOnlyList<string> PackageIds);

public sealed record PackageSelection(
    string PackageId,
    IReadOnlyList<string> Reasons);
