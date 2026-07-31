using System.Formats.Tar;
using System.Text;
using Lumina.BuildService.Services;
using Lumina.Shared.Errors;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class KubernetesArtifactBundleReaderTests
{
    [Fact]
    public async Task ExtractAsync_AcceptsOnlyManifestAndPlainRpmEntries()
    {
        var root = TemporaryRoot();
        try
        {
            await using var bundle = Bundle(
                ("manifest.json", "{\"version\":1}"),
                ("artifacts/kernel-1.0-1.aarch64.rpm", "rpm-one"),
                ("artifacts/kernel-devel-1.0-1.aarch64.rpm", "rpm-two"));

            var extracted = await KubernetesArtifactBundleReader.ExtractAsync(
                bundle,
                root,
                CancellationToken.None);

            Assert.Equal("{\"version\":1}", Encoding.UTF8.GetString(extracted.ManifestBytes));
            Assert.Equal(2, extracted.ArtifactPaths.Count);
            Assert.Equal(
                "rpm-one",
                await File.ReadAllTextAsync(extracted.ArtifactPaths["kernel-1.0-1.aarch64.rpm"]));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("../escape.rpm")]
    [InlineData("artifacts/../escape.rpm")]
    [InlineData("nested/pkg.rpm")]
    [InlineData("artifacts/nested/pkg.rpm")]
    [InlineData("artifacts/pkg.txt")]
    public async Task ExtractAsync_RejectsUnsafeOrUnknownPaths(string path)
    {
        var root = TemporaryRoot();
        try
        {
            await using var bundle = Bundle(
                ("manifest.json", "{}"),
                (path, "payload"));

            await Assert.ThrowsAsync<ValidationException>(() =>
                KubernetesArtifactBundleReader.ExtractAsync(
                    bundle,
                    root,
                    CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "escape.rpm")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ExtractAsync_RejectsLinksAndDuplicateManifest()
    {
        var root = TemporaryRoot();
        try
        {
            await using var linkBundle = new MemoryStream();
            using (var writer = new TarWriter(linkBundle, leaveOpen: true))
            {
                writer.WriteEntry(FileEntry("manifest.json", "{}"));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "artifacts/pkg.rpm")
                {
                    LinkName = "/etc/passwd"
                });
            }
            linkBundle.Position = 0;
            await Assert.ThrowsAsync<ValidationException>(() =>
                KubernetesArtifactBundleReader.ExtractAsync(
                    linkBundle,
                    root,
                    CancellationToken.None));

            await using var duplicate = Bundle(
                ("manifest.json", "{}"),
                ("manifest.json", "{}"),
                ("artifacts/pkg-1-1.noarch.rpm", "rpm"));
            await Assert.ThrowsAsync<ValidationException>(() =>
                KubernetesArtifactBundleReader.ExtractAsync(
                    duplicate,
                    root,
                    CancellationToken.None));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static MemoryStream Bundle(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, leaveOpen: true))
        {
            foreach (var entry in entries)
                writer.WriteEntry(FileEntry(entry.Name, entry.Content));
        }
        stream.Position = 0;
        return stream;
    }

    private static PaxTarEntry FileEntry(string name, string content) => new(
        TarEntryType.RegularFile,
        name)
    {
        DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
    };

    private static string TemporaryRoot() => Path.Combine(
        Path.GetTempPath(),
        "lumina-kubernetes-bundle-tests",
        Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
