using Lumina.BuildService.Services;
using Lumina.Shared.Errors;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class KubernetesArtifactObjectStoreTests
{
    [Fact]
    public async Task CopyBoundedAsync_CopiesExactBound()
    {
        await using var source = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        await using var destination = new MemoryStream();

        var copied = await KubernetesArtifactObjectStore.CopyBoundedAsync(
            source,
            destination,
            4,
            CancellationToken.None);

        Assert.Equal(4, copied);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, destination.ToArray());
    }

    [Fact]
    public async Task CopyBoundedAsync_RejectsOversizedStreamBeforeWritingPastBound()
    {
        await using var source = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
        await using var destination = new MemoryStream();

        await Assert.ThrowsAsync<ValidationException>(() =>
            KubernetesArtifactObjectStore.CopyBoundedAsync(
                source,
                destination,
                4,
                CancellationToken.None));

        Assert.True(destination.Length <= 4);
    }
}
