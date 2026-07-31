using Lumina.BuildService.Services.PackageGraph;
using Lumina.Shared.Errors;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class RepositorySnapshotStreamProviderTests
{
    [Fact]
    public void ValidateObjectIdentity_AcceptsContentAddressedProjectObject()
    {
        var projectId = Guid.NewGuid();
        var hash = new string('a', 64);

        RepositorySnapshotStreamProvider.ValidateObjectIdentity(
            projectId,
            $"project-{projectId:N}/{hash}/project-{projectId:N}-sources.tar.gz",
            hash,
            42);
    }

    [Theory]
    [InlineData("other/{hash}/snapshot.tar.gz")]
    [InlineData("project/{hash}/../snapshot.tar.gz")]
    [InlineData("project/{hash}/snapshot.zip")]
    [InlineData("project/wrong/snapshot.tar.gz")]
    public void ValidateObjectIdentity_RejectsUnexpectedObjectPath(string pathTemplate)
    {
        var projectId = Guid.NewGuid();
        var hash = new string('a', 64);
        var path = pathTemplate
            .Replace("project", $"project-{projectId:N}", StringComparison.Ordinal)
            .Replace("{hash}", hash, StringComparison.Ordinal);

        Assert.Throws<ValidationException>(() =>
            RepositorySnapshotStreamProvider.ValidateObjectIdentity(projectId, path, hash, 42));
    }
}
