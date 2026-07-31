using Lumina.BuildService.Services;
using Lumina.Shared.Models.Enums;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class CandidatePromotionSelectionTests
{
    [Fact]
    public void Resolve_RejectsCandidateModeWithoutKubernetesExecutor()
    {
        var configuration = Configuration(enabled: true);

        Assert.Throws<InvalidOperationException>(() =>
            CandidatePromotionSelection.Resolve(
                configuration, BuildExecutorBackend.Docker));
    }

    [Fact]
    public void Resolve_AllowsExplicitKubernetesCandidateMode()
    {
        var selection = CandidatePromotionSelection.Resolve(
            Configuration(enabled: true), BuildExecutorBackend.Kubernetes);

        Assert.True(selection.Enabled);
    }

    private static IConfiguration Configuration(bool enabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RepositoryPromotion:Enabled"] = enabled.ToString()
            })
            .Build();
}
