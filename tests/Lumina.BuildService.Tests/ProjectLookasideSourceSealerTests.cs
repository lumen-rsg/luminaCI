using System.Security.Cryptography;
using Lumina.BuildService.Services.PackageGraph;
using Lumina.Shared.Errors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class ProjectLookasideSourceSealerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lumina-lookaside-sealer-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SealAsync_UploadsOnlyVerifiedContentAddressedSnapshot()
    {
        var pipelineId = Guid.NewGuid();
        var bytes = "immutable payload"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var source = new ProjectLookasideSource(
            "payload.tar.gz",
            ProjectLookasideSourcePolicy.ObjectName("payload.tar.gz", hash),
            bytes.Length,
            hash);
        var path = CreateSource(pipelineId, source.FileName);
        await File.WriteAllBytesAsync(path, bytes);
        var objects = new MemoryObjectStore();

        await CreateSealer(objects).SealAsync(Plan(pipelineId, source), default);

        Assert.Equal(source.ObjectName, objects.ObjectName);
        Assert.Equal(bytes, objects.Bytes);
    }

    [Fact]
    public async Task SealAsync_RejectsDigestMismatchBeforeUpload()
    {
        var pipelineId = Guid.NewGuid();
        var source = new ProjectLookasideSource(
            "payload.tar.gz",
            ProjectLookasideSourcePolicy.ObjectName("payload.tar.gz", new string('a', 64)),
            7,
            new string('a', 64));
        var path = CreateSource(pipelineId, source.FileName);
        await File.WriteAllTextAsync(path, "payload");
        var objects = new MemoryObjectStore();

        await Assert.ThrowsAsync<ValidationException>(() =>
            CreateSealer(objects).SealAsync(Plan(pipelineId, source), default));

        Assert.Null(objects.ObjectName);
    }

    private ProjectLookasideSourceSealer CreateSealer(IProjectLookasideObjectStore objects)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ExtraSources:PipelinesRoot"] = _root }).Build();
        return new ProjectLookasideSourceSealer(
            configuration,
            objects,
            NullLogger<ProjectLookasideSourceSealer>.Instance);
    }

    private string CreateSource(Guid pipelineId, string fileName)
    {
        var directory = Path.Combine(_root, pipelineId.ToString(), "pipeline");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, fileName);
    }

    private static ProjectDispatchPlan Plan(Guid pipelineId, ProjectLookasideSource source) => new(
        [new ProjectDispatchStage(0, [new ProjectDispatchTarget(
            "package",
            pipelineId,
            "fedora-44-aarch64",
            "package/package.spec",
            "jetson-r39.2",
            [source])])],
        [],
        [],
        false);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class MemoryObjectStore : IProjectLookasideObjectStore
    {
        public string? ObjectName { get; private set; }
        public byte[]? Bytes { get; private set; }

        public async Task UploadAsync(
            string objectName,
            string sourcePath,
            long expectedSize,
            CancellationToken cancellationToken)
        {
            ObjectName = objectName;
            Bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            Assert.Equal(expectedSize, Bytes.LongLength);
        }
    }
}
