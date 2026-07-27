using Bunit;
using Lumina.Shared.DTOs;
using Lumina.Shared.Models.Enums;
using Lumina.WebApp.Components;
using Xunit;

namespace Lumina.WebApp.Tests;

public sealed class ComponentAccessibilityTests : IDisposable
{
    private readonly BunitContext _context = new();

    [Fact]
    public void Artifact_table_has_caption_scoped_headers_and_named_action()
    {
        var artifact = new BuildArtifactResponse(
            Guid.NewGuid(),
            "lumina-1.0-1.aarch64.rpm",
            4096,
            new string('a', 64),
            new string('b', 32),
            "FINGERPRINT",
            DateTime.UtcNow,
            ScanStatus.Completed);

        var cut = _context.Render<BuildArtifactsTable>(parameters => parameters
            .Add(component => component.Artifacts, [artifact]));

        Assert.Contains("verification status", cut.Find("caption").TextContent);
        Assert.Equal("row", cut.Find("tbody th").GetAttribute("scope"));
        Assert.Equal(
            $"Download {artifact.FileName}",
            cut.Find("button").GetAttribute("aria-label"));
    }

    [Fact]
    public void Pipeline_stages_use_labelled_ordered_list()
    {
        var build = new BuildJobResponse(
            Guid.NewGuid(),
            Guid.NewGuid(),
            BuildStatus.Building,
            "package.spec",
            null,
            "",
            DateTime.UtcNow,
            DateTime.UtcNow,
            null,
            "tests",
            [],
            StepRuns:
            [
                new(
                    Guid.NewGuid(),
                    StepType.Build,
                    "Build",
                    1,
                    StepStatus.Running,
                    DateTime.UtcNow,
                    null,
                    null)
            ]);

        var cut = _context.Render<BuildPipelineStages>(parameters => parameters
            .Add(component => component.Build, build));

        Assert.Equal(
            "pipeline-stages-title",
            cut.Find("section").GetAttribute("aria-labelledby"));
        Assert.Single(cut.FindAll("ol > li"));
        Assert.Contains("Running", cut.Find("ol > li").TextContent);
    }

    public void Dispose() => _context.Dispose();
}
