using Lumina.BuildService.Services;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class BuildSourceSecurityPolicyTests
{
    [Fact]
    public void Credential_free_sources_are_allowed()
    {
        BuildSourceSecurityPolicy.EnsureCredentialFree(
            "git://https://git.example.test/project.git#branch=main",
            null,
            null);
    }

    [Theory]
    [InlineData("builder", null)]
    [InlineData(null, "secret-token")]
    [InlineData("builder", "secret-token")]
    public void Separate_git_credentials_are_rejected(string? username, string? token)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BuildSourceSecurityPolicy.EnsureCredentialFree(
                "git://https://git.example.test/project.git#branch=main",
                username,
                token));

        Assert.Contains("trusted source service", exception.Message);
    }

    [Theory]
    [InlineData("https://builder:secret@git.example.test/project.git")]
    [InlineData("git://https://builder:secret@git.example.test/project.git#branch=main")]
    public void Credentials_embedded_in_source_url_are_rejected(string sourceUrl)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BuildSourceSecurityPolicy.EnsureCredentialFree(sourceUrl, null, null));

        Assert.Contains("containing credentials", exception.Message);
    }
}
