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

    [Theory]
    [InlineData("lumina-rpm-build:latest")]
    [InlineData("lumina-rpm-build")]
    public void Resolve_RejectsMutableImageReferences(string image)
    {
        var configuration = Configuration(image);

        var exception = Assert.Throws<ValidationException>(
            () => BuildImagePolicy.Resolve(configuration, image));

        Assert.Contains("immutable digest or an explicit version tag", exception.Message);
    }

    [Fact]
    public void BuildTarget_requires_canonical_reviewed_profile()
    {
        var target = BuildTargetPolicy.Resolve(
            "Fedora", "44", "AARCH64", "fedora-44-aarch64");

        Assert.Equal("fedora", target.Distribution);
        Assert.Equal("aarch64", target.Architecture);
        Assert.Throws<ValidationException>(() =>
            BuildTargetPolicy.Resolve("fedora", "44", "aarch64", "fedora-44-x86_64"));
        Assert.Throws<ValidationException>(() =>
            BuildTargetPolicy.Resolve("fedora", "45", "aarch64", "fedora-45-aarch64"));
        Assert.Throws<ValidationException>(() =>
            BuildTargetPolicy.Resolve(null, "44", "aarch64", "fedora-44-aarch64"));
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
