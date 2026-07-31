using Lumina.BuildService.Services.PackageGraph;
using Lumina.Shared.Errors;
using Xunit;

namespace Lumina.BuildService.Tests;

public class PackageGraphManifestLoaderTests
{
    private static readonly IReadOnlySet<string> SupportedTargets =
        new HashSet<string>(StringComparer.Ordinal) { "fedora-44-aarch64" };

    [Fact]
    public void LoadAndPlan_ParsesDocumentAndBuildsSelection()
    {
        var plan = PackageGraphManifestLoader.LoadAndPlan("""
            version: 1
            packages:
              firmware:
                spec: jetson/specs/firmware.spec
                paths:
                  - jetson/firmware/**
                targets:
                  - fedora-44-aarch64
                promotion_group: jetson-r39.2
                lookaside_sources:
                  - file: tegra-l4t-firmware-39.2.0.tar.gz
                    size: 66183345
                    sha256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
              driver:
                spec: jetson/specs/driver.spec
                paths:
                  - jetson/driver/**
                targets:
                  - fedora-44-aarch64
                depends_on:
                  - firmware
                promotion_group: jetson-r39.2
            """, ["jetson/firmware/tegra.bin"], SupportedTargets);

        Assert.Equal(["driver", "firmware"], plan.Selected.Select(item => item.PackageId).ToArray());
        Assert.Equal(["firmware"], plan.Stages[0].PackageIds);
        Assert.Equal(["driver"], plan.Stages[1].PackageIds);
        var graph = PackageGraphManifestLoader.Load("""
            version: 1
            packages:
              firmware:
                spec: jetson/specs/firmware.spec
                paths: [jetson/firmware/**]
                targets: [fedora-44-aarch64]
                lookaside_sources:
                  - file: firmware.tar.gz
                    size: 42
                    sha256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
            """);
        var source = Assert.Single(Assert.Single(graph.Packages).LookasideSources!);
        Assert.Equal("firmware.tar.gz", source.FileName);
        Assert.Equal(42, source.Size);
    }

    [Fact]
    public void Load_DefaultsToRebuildingDependents()
    {
        var graph = PackageGraphManifestLoader.Load("""
            version: 1
            packages:
              package:
                spec: package/package.spec
                paths: [package/**]
                targets: [fedora-44-aarch64]
            """);

        Assert.True(Assert.Single(graph.Packages).RebuildOnDependencyChange);
    }

    [Fact]
    public void Load_RejectsDuplicateAndUnknownKeys()
    {
        var duplicate = """
            version: 1
            version: 1
            packages: {}
            """;
        Assert.Throws<ValidationException>(() => PackageGraphManifestLoader.Load(duplicate));

        var unknown = """
            version: 1
            unexpected: true
            packages: {}
            """;
        Assert.Throws<ValidationException>(() => PackageGraphManifestLoader.Load(unknown));
    }

    [Fact]
    public void Load_RejectsMalformedAndEmptyDocuments()
    {
        Assert.Throws<ValidationException>(() => PackageGraphManifestLoader.Load(""));
        Assert.Throws<ValidationException>(() =>
            PackageGraphManifestLoader.Load("version: [not-a-number]"));
    }

    [Fact]
    public void Load_RejectsAnchorsAndAliases()
    {
        var yaml = """
            version: 1
            packages:
              one: &shared
                spec: one/one.spec
                paths: [one/**]
                targets: [fedora-44-aarch64]
              two: *shared
            """;

        var error = Assert.Throws<ValidationException>(() =>
            PackageGraphManifestLoader.Load(yaml));

        Assert.Contains("anchors or aliases", error.Message);
    }

    [Fact]
    public void Load_RejectsOversizedDocumentBeforeParsing()
    {
        var oversized = "#" + new string('x', PackageGraphManifestLoader.MaxManifestBytes);

        var error = Assert.Throws<ValidationException>(() =>
            PackageGraphManifestLoader.Load(oversized));

        Assert.Contains("exceeds", error.Message);
    }

    [Fact]
    public void LoadAndPlan_RejectsUnsafeLookasideIdentity()
    {
        var yaml = """
            version: 1
            packages:
              package:
                spec: package/package.spec
                paths: [package/**]
                targets: [fedora-44-aarch64]
                lookaside_sources:
                  - file: ../payload.tar.gz
                    size: 1
                    sha256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
            """;

        Assert.Throws<ValidationException>(() =>
            PackageGraphManifestLoader.LoadAndPlan(yaml, ["package/file"], SupportedTargets));
    }
}
