using Lumina.BuildService.Services.PackageGraph;
using Lumina.Shared.Errors;
using Xunit;

namespace Lumina.BuildService.Tests;

public class PackageGraphPlannerTests
{
    private static readonly IReadOnlySet<string> SupportedTargets =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "fedora-44-aarch64",
            "fedora-44-x86_64"
        };

    [Fact]
    public void Plan_SelectsChangedPackageAndReverseDependentsInStages()
    {
        var plan = PackageGraphPlanner.Plan(
            JetsonGraph(),
            ["jetson/firmware/tegra.bin"],
            SupportedTargets);

        Assert.False(plan.IsConservative);
        Assert.Equal(
            [
                ["tegra-l4t-firmware"],
                ["kernel-tegra-l4t"],
                ["nvidia-l4t-driver"],
                ["nvidia-l4t-multimedia", "nvidia-l4t-tools"]
            ],
            plan.Stages.Select(stage => stage.PackageIds).ToArray());
        Assert.Equal(["lumina-jetson-bootconf"], plan.Skipped);
        Assert.Contains(
            plan.Selected.Single(item => item.PackageId == "kernel-tegra-l4t").Reasons,
            reason => reason == "dependency selected: tegra-l4t-firmware");
    }

    [Fact]
    public void Plan_DoesNotRebuildDependenciesOfDirectlyChangedPackage()
    {
        var plan = PackageGraphPlanner.Plan(
            JetsonGraph(),
            ["jetson/driver/nvidia.ko"],
            SupportedTargets);

        Assert.Equal(
            ["nvidia-l4t-driver", "nvidia-l4t-multimedia", "nvidia-l4t-tools"],
            plan.Selected.Select(item => item.PackageId).ToArray());
        Assert.DoesNotContain("kernel-tegra-l4t", plan.Selected.Select(item => item.PackageId));
    }

    [Fact]
    public void Plan_ManifestChangeSelectsEverythingConservatively()
    {
        var plan = PackageGraphPlanner.Plan(
            JetsonGraph(),
            [PackageGraphPlanner.ManifestPath],
            SupportedTargets);

        Assert.True(plan.IsConservative);
        Assert.Empty(plan.Skipped);
        Assert.All(plan.Selected, selection =>
            Assert.Contains($"manifest changed: {PackageGraphPlanner.ManifestPath}", selection.Reasons));
    }

    [Fact]
    public void Plan_MissingChangedPathsSelectsEverythingConservatively()
    {
        var plan = PackageGraphPlanner.Plan(JetsonGraph(), [], SupportedTargets);

        Assert.True(plan.IsConservative);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Plan_UnrelatedChangeSelectsNothing()
    {
        var plan = PackageGraphPlanner.Plan(
            JetsonGraph(),
            ["README.md"],
            SupportedTargets);

        Assert.False(plan.IsConservative);
        Assert.Empty(plan.Stages);
        Assert.Empty(plan.Selected);
        Assert.Equal(6, plan.Skipped.Count);
    }

    [Fact]
    public void Plan_RespectsDependentOptOut()
    {
        var graph = new RepositoryPackageGraph(1,
        [
            Package("library", "library/**"),
            Package("application", "application/**", ["library"], rebuildOnDependencyChange: false)
        ]);

        var plan = PackageGraphPlanner.Plan(graph, ["library/source.c"], SupportedTargets);

        Assert.Equal(["library"], plan.Selected.Select(item => item.PackageId).ToArray());
    }

    [Fact]
    public void Plan_RejectsDependencyCycle()
    {
        var graph = new RepositoryPackageGraph(1,
        [
            Package("one", "one/**", ["two"]),
            Package("two", "two/**", ["one"])
        ]);

        var error = Assert.Throws<ValidationException>(() =>
            PackageGraphPlanner.Plan(graph, ["one/file"], SupportedTargets));

        Assert.Contains("one -> two -> one", error.Message);
    }

    [Fact]
    public void Plan_RejectsUnknownDependencyAndTarget()
    {
        var missingDependency = new RepositoryPackageGraph(1,
        [
            Package("one", "one/**", ["missing"])
        ]);
        Assert.Throws<ValidationException>(() =>
            PackageGraphPlanner.Plan(missingDependency, ["one/file"], SupportedTargets));

        var unknownTarget = new RepositoryPackageGraph(1,
        [
            new RepositoryPackageDefinition(
                "one", "one/one.spec", ["one/**"], ["fedora-45-aarch64"])
        ]);
        Assert.Throws<ValidationException>(() =>
            PackageGraphPlanner.Plan(unknownTarget, ["one/file"], SupportedTargets));
    }

    [Fact]
    public void Plan_RejectsUnsafePathAndAmbiguousSpecOwnership()
    {
        var unsafePath = new RepositoryPackageGraph(1,
        [
            Package("one", "../one/**")
        ]);
        Assert.Throws<ValidationException>(() =>
            PackageGraphPlanner.Plan(unsafePath, ["one/file"], SupportedTargets));

        var sharedSpec = new RepositoryPackageGraph(1,
        [
            Package("one", "one/**", specPath: "shared/package.spec"),
            Package("two", "two/**", specPath: "shared/package.spec")
        ]);
        Assert.Throws<ValidationException>(() =>
            PackageGraphPlanner.Plan(sharedSpec, ["one/file"], SupportedTargets));
    }

    private static RepositoryPackageGraph JetsonGraph() => new(1,
    [
        Package("lumina-jetson-bootconf", "jetson/boot/**"),
        Package("tegra-l4t-firmware", "jetson/firmware/**"),
        Package("kernel-tegra-l4t", "jetson/kernel/**", ["tegra-l4t-firmware"]),
        Package("nvidia-l4t-driver", "jetson/driver/**", ["kernel-tegra-l4t", "tegra-l4t-firmware"]),
        Package("nvidia-l4t-multimedia", "jetson/multimedia/**", ["nvidia-l4t-driver"]),
        Package("nvidia-l4t-tools", "jetson/tools/**", ["nvidia-l4t-driver"])
    ]);

    private static RepositoryPackageDefinition Package(
        string id,
        string path,
        IReadOnlyList<string>? dependencies = null,
        bool rebuildOnDependencyChange = true,
        string? specPath = null) => new(
            id,
            specPath ?? $"{id}/{id}.spec",
            [path],
            ["fedora-44-aarch64"],
            dependencies,
            "jetson-r39.2",
            rebuildOnDependencyChange);
}
