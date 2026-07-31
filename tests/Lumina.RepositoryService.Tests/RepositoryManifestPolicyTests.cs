using Lumina.RepositoryService.Services;
using Lumina.Shared.Models;
using Xunit;

namespace Lumina.RepositoryService.Tests;

public sealed class RepositoryManifestPolicyTests
{
    [Fact]
    public void Manifest_IsOrderIndependentButBindsEveryPackageByteIdentity()
    {
        var first = Package("kernel", "aarch64", 'a');
        var second = Package("firmware", "noarch", 'b');

        var expected = RepositoryManifestPolicy.Compute([first, second]);

        Assert.Equal(expected, RepositoryManifestPolicy.Compute([second, first]));
        second.HashSha256 = new string('c', 64);
        Assert.NotEqual(expected, RepositoryManifestPolicy.Compute([first, second]));
    }

    private static Package Package(string name, string arch, char hash) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Version = "1",
        Release = "1",
        Arch = arch,
        FileName = $"{name}-1-1.{arch}.rpm",
        FileSize = 42,
        HashSha256 = new string(hash, 64)
    };
}
