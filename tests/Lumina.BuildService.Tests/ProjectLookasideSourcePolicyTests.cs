using Lumina.BuildService.Services.PackageGraph;
using Lumina.Shared.Errors;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class ProjectLookasideSourcePolicyTests
{
    [Fact]
    public void Normalize_CreatesContentAddressedIdentity()
    {
        var hash = new string('a', 64);

        var source = Assert.Single(ProjectLookasideSourcePolicy.Normalize(
            [new RepositoryLookasideSource("payload.tar.gz", 42, hash.ToUpperInvariant())]));

        Assert.Equal("payload.tar.gz", source.FileName);
        Assert.Equal(hash, source.Sha256);
        Assert.Equal($"lookaside/sha256/{hash}/payload.tar.gz", source.ObjectName);
    }

    [Theory]
    [InlineData("../payload.tar.gz", 42, true)]
    [InlineData(".", 42, true)]
    [InlineData("payload.tar.gz", 0, true)]
    [InlineData("payload.tar.gz", 42, false)]
    public void Normalize_RejectsInvalidIdentity(string fileName, long size, bool validHash)
    {
        var hash = validHash ? new string('a', 64) : "bad";

        Assert.Throws<ValidationException>(() => ProjectLookasideSourcePolicy.Normalize(
            [new RepositoryLookasideSource(fileName, size, hash)]));
    }
}
