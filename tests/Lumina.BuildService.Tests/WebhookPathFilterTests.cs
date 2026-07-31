using System.Text.Json;
using Lumina.BuildService.Services;
using Lumina.Shared.Errors;
using Xunit;

namespace Lumina.BuildService.Tests;

public class WebhookPathFilterTests
{
    [Fact]
    public void Normalize_DefaultsToSpecDirectory()
    {
        var filters = WebhookPathFilter.Normalize(
            null, "common/lumina-release/lumina-release.spec");

        Assert.Equal(["common/lumina-release"], filters);
    }

    [Fact]
    public void Normalize_RejectsTraversal()
    {
        Assert.Throws<ValidationException>(() =>
            WebhookPathFilter.Normalize(["common/../secrets"], "pkg/pkg.spec"));
    }

    [Fact]
    public void ExtractChangedPaths_ReadsGitHubCommitMetadata()
    {
        using var payload = JsonDocument.Parse("""
            {
              "commits": [
                {"added":["common/neofetch/new.patch"],"modified":["README.md"],"removed":[]}
              ],
              "head_commit": {"modified":["common/neofetch/neofetch.spec"]}
            }
            """);

        var changed = WebhookPathFilter.ExtractChangedPaths(payload.RootElement);

        Assert.Contains("common/neofetch/new.patch", changed);
        Assert.Contains("common/neofetch/neofetch.spec", changed);
        Assert.Contains("README.md", changed);
    }

    [Theory]
    [InlineData("common/neofetch", "common/neofetch/neofetch.spec", true)]
    [InlineData("common/neofetch", "common/neofetch-extra/file", false)]
    [InlineData("common/neofetch/neofetch.spec", "common/neofetch/neofetch.spec", true)]
    public void MatchesAny_UsesPathSegmentBoundaries(
        string filter, string changed, bool expected)
    {
        Assert.Equal(expected, WebhookPathFilter.MatchesAny([filter], [changed]));
    }

    [Fact]
    public void MatchesAny_TriggersConservativelyWhenProviderOmitsPaths()
    {
        Assert.True(WebhookPathFilter.MatchesAny(["common/neofetch"], []));
    }
}
