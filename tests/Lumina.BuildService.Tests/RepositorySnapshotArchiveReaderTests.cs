using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Lumina.BuildService.Services.PackageGraph;
using Lumina.Shared.Errors;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class RepositorySnapshotArchiveReaderTests
{
    [Fact]
    public async Task ReadManifestAsync_ReadsNestedManifestAndVerifiesArchive()
    {
        var archive = CreateArchive(
            ("project-wrap/README.md", "readme", TarEntryType.RegularFile),
            ("project-wrap/.lumina/packages.yaml", "version: 1\npackages: {}\n", TarEntryType.RegularFile));

        var manifest = await ReadAsync(archive, ".lumina/packages.yaml");

        Assert.Equal("version: 1\npackages: {}\n", manifest);
    }

    [Fact]
    public async Task ReadManifestAsync_RejectsDigestAndSizeMismatch()
    {
        var archive = CreateArchive((".lumina/packages.yaml", "version: 1", TarEntryType.RegularFile));
        var hash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();

        await Assert.ThrowsAsync<ValidationException>(() =>
            RepositorySnapshotArchiveReader.ReadManifestAsync(
                new MemoryStream(archive), archive.Length + 1, hash, ".lumina/packages.yaml"));
        await Assert.ThrowsAsync<ValidationException>(() =>
            RepositorySnapshotArchiveReader.ReadManifestAsync(
                new MemoryStream(archive), archive.Length, new string('0', 64), ".lumina/packages.yaml"));
    }

    [Fact]
    public async Task ReadManifestAsync_RejectsDuplicateManifestAndLinks()
    {
        var duplicate = CreateArchive(
            ("one/.lumina/packages.yaml", "version: 1", TarEntryType.RegularFile),
            ("two/.lumina/packages.yaml", "version: 1", TarEntryType.RegularFile));
        await Assert.ThrowsAsync<ValidationException>(() =>
            ReadAsync(duplicate, ".lumina/packages.yaml"));

        var link = CreateArchive(("manifest-link", ".lumina/packages.yaml", TarEntryType.SymbolicLink));
        await Assert.ThrowsAsync<ValidationException>(() => ReadAsync(link, "manifest-link"));
    }

    [Fact]
    public async Task ReadManifestAsync_RejectsTraversalAndInvalidUtf8()
    {
        var traversal = CreateArchive(("../.lumina/packages.yaml", "bad", TarEntryType.RegularFile));
        await Assert.ThrowsAsync<ValidationException>(() =>
            ReadAsync(traversal, ".lumina/packages.yaml"));

        var invalidUtf8 = CreateArchiveBytes(
            (".lumina/packages.yaml", new byte[] { 0xff, 0xfe }, TarEntryType.RegularFile));
        await Assert.ThrowsAsync<ValidationException>(() =>
            ReadAsync(invalidUtf8, ".lumina/packages.yaml"));
    }

    private static Task<string> ReadAsync(byte[] archive, string manifestPath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        return RepositorySnapshotArchiveReader.ReadManifestAsync(
            new MemoryStream(archive), archive.Length, hash, manifestPath);
    }

    private static byte[] CreateArchive(
        params (string Path, string Content, TarEntryType Type)[] entries) =>
        CreateArchiveBytes(entries.Select(entry =>
            (entry.Path, Encoding.UTF8.GetBytes(entry.Content), entry.Type)).ToArray());

    private static byte[] CreateArchiveBytes(
        params (string Path, byte[] Content, TarEntryType Type)[] entries)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            foreach (var (path, content, type) in entries)
            {
                PaxTarEntry entry = type switch
                {
                    TarEntryType.SymbolicLink => new PaxTarEntry(type, path) { LinkName = Encoding.UTF8.GetString(content) },
                    _ => new PaxTarEntry(type, path) { DataStream = new MemoryStream(content) }
                };
                writer.WriteEntry(entry);
            }
        }
        return output.ToArray();
    }
}
