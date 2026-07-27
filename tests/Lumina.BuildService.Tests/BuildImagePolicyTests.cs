using Lumina.BuildService.Services;
using Lumina.Shared.Errors;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Lumina.BuildService.Tests;

public class BuildImagePolicyTests
{
    [Fact]
    public void Resolve_RejectsImageOutsideOperatorAllowList()
    {
        var configuration = Configuration("lumina-rpm-build@sha256:abc");

        var exception = Assert.Throws<ValidationException>(
            () => BuildImagePolicy.Resolve(configuration, "attacker/image:latest"));

        Assert.Contains("not an allowed", exception.Message);
    }

    [Fact]
    public void Resolve_AcceptsExactConfiguredImage()
    {
        const string image = "registry.example/lumina-rpm-build@sha256:abc";
        var configuration = Configuration(image);

        Assert.Equal(image, BuildImagePolicy.Resolve(configuration, image));
    }

    [Fact]
    public void Resolve_FailsClosedWhenAllowListIsMissing()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<InvalidOperationException>(
            () => BuildImagePolicy.Resolve(configuration, null));
    }

    private static IConfiguration Configuration(params string[] images)
    {
        var values = images
            .Select((image, index) =>
                new KeyValuePair<string, string?>($"Docker:AllowedBuildImages:{index}", image));

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
