using Lumina.BuildService.Services.PackageGraph;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class ProjectDispatchPlanResolverTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    [Fact]
    public void Resolve_MapsDependencyStagesToBoundPipelines()
    {
        var firmware = Pipeline("firmware", "specs/firmware.spec");
        var driver = Pipeline("driver", "specs/driver.spec");

        var plan = ProjectDispatchPlanResolver.Resolve(
            Manifest(), ["firmware/blob.bin"], [driver, firmware]);

        Assert.Equal(2, plan.Stages.Count);
        var firmwareTarget = Assert.Single(plan.Stages[0].Targets);
        Assert.Equal(firmware.Id, firmwareTarget.PipelineId);
        Assert.Equal("firmware", firmwareTarget.PromotionGroup);
        var driverTarget = Assert.Single(plan.Stages[1].Targets);
        Assert.Equal(driver.Id, driverTarget.PipelineId);
        Assert.Equal("jetson-r39.2", driverTarget.PromotionGroup);
        Assert.False(plan.IsConservative);
    }

    [Fact]
    public void Resolve_UnrelatedChangeProducesEmptyStages()
    {
        var plan = ProjectDispatchPlanResolver.Resolve(
            Manifest(),
            ["README.md"],
            [Pipeline("firmware", "specs/firmware.spec"), Pipeline("driver", "specs/driver.spec")]);

        Assert.Empty(plan.Stages);
        Assert.Equal(["driver", "firmware"], plan.Skipped);
    }

    [Fact]
    public void Resolve_RejectsMissingOrUnknownBindings()
    {
        Assert.Throws<ValidationException>(() =>
            ProjectDispatchPlanResolver.Resolve(
                Manifest(), ["firmware/blob"], [Pipeline("firmware", "specs/firmware.spec")]));
        Assert.Throws<ValidationException>(() =>
            ProjectDispatchPlanResolver.Resolve(
                Manifest(), ["firmware/blob"],
                [
                    Pipeline("firmware", "specs/firmware.spec"),
                    Pipeline("driver", "specs/driver.spec"),
                    Pipeline("unknown", "specs/unknown.spec")
                ]));
    }

    [Fact]
    public void Resolve_RejectsTargetAndSpecMismatch()
    {
        var wrongTarget = Pipeline("firmware", "specs/firmware.spec");
        wrongTarget.BuildProfile = "fedora-44-x86_64";
        Assert.Throws<ValidationException>(() =>
            ProjectDispatchPlanResolver.Resolve(
                Manifest(), ["firmware/blob"],
                [wrongTarget, Pipeline("driver", "specs/driver.spec")]));

        Assert.Throws<ValidationException>(() =>
            ProjectDispatchPlanResolver.Resolve(
                Manifest(), ["firmware/blob"],
            [Pipeline("firmware", "wrong.spec"), Pipeline("driver", "specs/driver.spec")]));
    }

    [Fact]
    public void Resolve_RejectsCredentialedRunnerBinding()
    {
        var firmware = Pipeline("firmware", "specs/firmware.spec");
        firmware.GitUsername = "git";
        firmware.GitToken = "secret";

        Assert.Throws<ValidationException>(() =>
            ProjectDispatchPlanResolver.Resolve(
                Manifest(), ["firmware/blob"],
                [firmware, Pipeline("driver", "specs/driver.spec")]));
    }

    private static Pipeline Pipeline(string packageId, string specPath) => new()
    {
        Id = Guid.NewGuid(),
        BuildProjectId = ProjectId,
        PackageId = packageId,
        Name = packageId,
        Status = PipelineStatus.Active,
        SpecPath = specPath,
        BuildProfile = "fedora-44-aarch64"
    };

    private static string Manifest() => """
        version: 1
        packages:
          firmware:
            spec: specs/firmware.spec
            paths: [firmware/**]
            targets: [fedora-44-aarch64]
          driver:
            spec: specs/driver.spec
            paths: [driver/**]
            targets: [fedora-44-aarch64]
            depends_on: [firmware]
            promotion_group: jetson-r39.2
        """;
}
