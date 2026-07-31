using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class KubernetesArtifactImporterTests
{
    [Fact]
    public async Task ImportAsync_VerifiesPersistsAndReplaysIdempotently()
    {
        var root = TemporaryRoot();
        try
        {
            await using var db = Context();
            var job = Job();
            db.BuildJobs.Add(job);
            await db.SaveChangesAsync();
            var bytes = new byte[] { 0xed, 0xab, 0xee, 0xdb, 1, 2, 3, 4 };
            var objects = Store(job, "kernel-1.0-1.aarch64.rpm", bytes);
            var importer = Importer(db, objects, root, "aarch64");

            Assert.Equal(1, await importer.ImportAsync(job.Id, CancellationToken.None));
            Assert.Equal(1, await importer.ImportAsync(job.Id, CancellationToken.None));

            var persisted = await db.BuildJobs
                .Include(item => item.Artifacts)
                .SingleAsync(item => item.Id == job.Id);
            var artifact = Assert.Single(persisted.Artifacts);
            Assert.Equal(objects.Entry.ObjectName, artifact.SourceStoragePath);
            Assert.Equal(objects.Entry.Sha256, artifact.HashSha256);
            Assert.Equal("kernel-0:1.0-1.aarch64", artifact.RpmNevra);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(artifact.FilePath));
            Assert.Equal(1, objects.DownloadCount);
            Assert.Equal(1, objects.ArtifactUploadCount);
            Assert.Equal(1, objects.ManifestUploadCount);
            Assert.NotNull(persisted.KubernetesArtifactsImportedAt);
            Assert.Equal(64, persisted.KubernetesArtifactManifestSha256?.Length);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ImportAsync_RejectsDigestMismatchWithoutRecordingArtifacts()
    {
        var root = TemporaryRoot();
        try
        {
            await using var db = Context();
            var job = Job();
            db.BuildJobs.Add(job);
            await db.SaveChangesAsync();
            var objects = Store(
                job,
                "kernel-1.0-1.aarch64.rpm",
                new byte[] { 0xed, 0xab, 0xee, 0xdb, 1 });
            objects.ArtifactBytes = new byte[] { 0xed, 0xab, 0xee, 0xdb, 2 };

            var exception = await Assert.ThrowsAsync<ValidationException>(() =>
                Importer(db, objects, root, "aarch64")
                    .ImportAsync(job.Id, CancellationToken.None));

            Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
            Assert.Empty(db.BuildArtifacts);
            Assert.Null(job.KubernetesArtifactsImportedAt);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ImportAsync_RejectsRpmArchitectureMismatch()
    {
        var root = TemporaryRoot();
        try
        {
            await using var db = Context();
            var job = Job();
            db.BuildJobs.Add(job);
            await db.SaveChangesAsync();
            var objects = Store(
                job,
                "kernel-1.0-1.aarch64.rpm",
                new byte[] { 0xed, 0xab, 0xee, 0xdb, 1 });

            var exception = await Assert.ThrowsAsync<ValidationException>(() =>
                Importer(db, objects, root, "x86_64")
                    .ImportAsync(job.Id, CancellationToken.None));

            Assert.Contains("does not match target", exception.Message, StringComparison.Ordinal);
            Assert.Empty(db.BuildArtifacts);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ImportAsync_RejectsFilenameThatDoesNotMatchRpmIdentity()
    {
        var root = TemporaryRoot();
        try
        {
            await using var db = Context();
            var job = Job();
            db.BuildJobs.Add(job);
            await db.SaveChangesAsync();
            var objects = Store(
                job,
                "renamed-package.aarch64.rpm",
                new byte[] { 0xed, 0xab, 0xee, 0xdb, 1 });

            var exception = await Assert.ThrowsAsync<ValidationException>(() =>
                Importer(db, objects, root, "aarch64")
                    .ImportAsync(job.Id, CancellationToken.None));

            Assert.Contains("does not match RPM identity", exception.Message, StringComparison.Ordinal);
            Assert.Empty(db.BuildArtifacts);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ImportAsync_RejectsUnknownManifestFields()
    {
        var root = TemporaryRoot();
        try
        {
            await using var db = Context();
            var job = Job();
            db.BuildJobs.Add(job);
            await db.SaveChangesAsync();
            var objects = Store(
                job,
                "kernel-1.0-1.aarch64.rpm",
                new byte[] { 0xed, 0xab, 0xee, 0xdb, 1 });
            var json = JsonSerializer.SerializeToUtf8Bytes(Manifest(job, objects.Entry));
            var text = System.Text.Encoding.UTF8.GetString(json);
            objects.ManifestBytes = System.Text.Encoding.UTF8.GetBytes(
                text[..^1] + ",\"unexpected\":true}");

            var exception = await Assert.ThrowsAsync<ValidationException>(() =>
                Importer(db, objects, root, "aarch64")
                    .ImportAsync(job.Id, CancellationToken.None));

            Assert.Contains("JSON is invalid", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, objects.DownloadCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ImportAsync_RejectsBundleThatDoesNotExactlyMatchManifest()
    {
        var root = TemporaryRoot();
        try
        {
            await using var db = Context();
            var job = Job();
            db.BuildJobs.Add(job);
            await db.SaveChangesAsync();
            var objects = Store(
                job,
                "kernel-1.0-1.aarch64.rpm",
                new byte[] { 0xed, 0xab, 0xee, 0xdb, 1 });
            objects.BundleFileName = "unexpected-1.0-1.aarch64.rpm";

            var exception = await Assert.ThrowsAsync<ValidationException>(() =>
                Importer(db, objects, root, "aarch64")
                    .ImportAsync(job.Id, CancellationToken.None));

            Assert.Contains("exactly match", exception.Message, StringComparison.Ordinal);
            Assert.Empty(db.BuildArtifacts);
            Assert.Equal(0, objects.ArtifactUploadCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static KubernetesArtifactImporter Importer(
        BuildDbContext db,
        FakeObjectStore objects,
        string root,
        string architecture) => new(
        db,
        objects,
        new FakeRpmValidator(architecture),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kubernetes:Artifacts:LocalRoot"] = root
        }).Build(),
        NullLogger<KubernetesArtifactImporter>.Instance);

    private static FakeObjectStore Store(BuildJob job, string fileName, byte[] bytes)
    {
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var entry = new KubernetesArtifactManifestEntry(
            fileName,
            KubernetesArtifactManifestPolicy.ObjectName(
                job.Id,
                job.KubernetesJobUid!,
                digest,
                fileName),
            bytes.LongLength,
            digest);
        return new FakeObjectStore
        {
            Entry = entry,
            BundleFileName = fileName,
            ArtifactBytes = bytes,
            ManifestBytes = JsonSerializer.SerializeToUtf8Bytes(Manifest(job, entry))
        };
    }

    private static KubernetesArtifactManifest Manifest(
        BuildJob job,
        KubernetesArtifactManifestEntry entry) => new(
        KubernetesArtifactManifestPolicy.CurrentVersion,
        job.Id,
        job.KubernetesJobUid!,
        job.RunnerImageDigest!,
        job.TargetDistribution,
        job.TargetRelease,
        job.TargetArchitecture,
        [entry]);

    private static BuildJob Job() => new()
    {
        Id = Guid.NewGuid(),
        PipelineId = Guid.NewGuid(),
        ExecutionBackend = BuildExecutorBackend.Kubernetes,
        Status = BuildStatus.Building,
        SpecName = "kernel-tegra.spec",
        BuildProfile = "fedora-44-aarch64",
        TargetDistribution = "fedora",
        TargetRelease = "44",
        TargetArchitecture = "aarch64",
        RunnerImageDigest = "sha256:" + new string('a', 64),
        KubernetesNamespace = "lumina-builds",
        KubernetesJobName = "lumina-build-test",
        KubernetesJobUid = "12345678-1234-1234-1234-123456789abc"
    };

    private static TestBuildDbContext Context()
    {
        var options = new DbContextOptionsBuilder<BuildDbContext>()
            .UseInMemoryDatabase($"kubernetes-artifact-import-{Guid.NewGuid():N}")
            .Options;
        return new TestBuildDbContext(options);
    }

    private static string TemporaryRoot() => Path.Combine(
        Path.GetTempPath(),
        "lumina-kubernetes-artifact-tests",
        Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class FakeObjectStore : IKubernetesArtifactObjectStore
    {
        public required byte[] ManifestBytes { get; set; }
        public required byte[] ArtifactBytes { get; set; }
        public required KubernetesArtifactManifestEntry Entry { get; init; }
        public required string BundleFileName { get; set; }
        public int DownloadCount { get; private set; }
        public int ArtifactUploadCount { get; private set; }
        public int ManifestUploadCount { get; private set; }

        public Task<byte[]> ReadManifestAsync(
            string objectName,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            Assert.EndsWith("/manifest.json", objectName, StringComparison.Ordinal);
            Assert.True(ManifestBytes.Length <= maximumBytes);
            return Task.FromResult(ManifestBytes);
        }

        public async Task DownloadBundleAsync(
            string objectName,
            string destinationPath,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            Assert.Contains("/bundle.tar", objectName, StringComparison.Ordinal);
            DownloadCount++;
            await using var stream = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            using var writer = new TarWriter(stream, leaveOpen: true);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "manifest.json")
            {
                DataStream = new MemoryStream(ManifestBytes)
            });
            writer.WriteEntry(new PaxTarEntry(
                TarEntryType.RegularFile,
                $"artifacts/{BundleFileName}")
            {
                DataStream = new MemoryStream(ArtifactBytes)
            });
            await stream.FlushAsync(cancellationToken);
            Assert.True(stream.Length <= maximumBytes);
        }

        public async Task UploadVerifiedArtifactAsync(
            string objectName,
            string sourcePath,
            long expectedBytes,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Entry.ObjectName, objectName);
            Assert.Equal(Entry.Size, expectedBytes);
            Assert.Equal(Entry.Sha256, expectedSha256);
            Assert.Equal(ArtifactBytes, await File.ReadAllBytesAsync(sourcePath, cancellationToken));
            ArtifactUploadCount++;
        }

        public Task UploadManifestAsync(
            string objectName,
            byte[] manifestBytes,
            CancellationToken cancellationToken)
        {
            Assert.EndsWith("/manifest.json", objectName, StringComparison.Ordinal);
            ManifestBytes = manifestBytes;
            ManifestUploadCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRpmValidator(string architecture) : IRpmArtifactValidator
    {
        public Task<RpmValidationResult> ValidateAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RpmValidationResult.Valid(
                $"kernel-0:1.0-1.{architecture}",
                architecture,
                $"kernel-1.0-1.{architecture}.rpm"));
    }

    private sealed class TestBuildDbContext(DbContextOptions<BuildDbContext> options)
        : BuildDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Pipeline>().Property(item => item.Tags).HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<List<string>>(value, (JsonSerializerOptions?)null) ?? new List<string>());
            ConfigureDictionary(modelBuilder.Entity<PipelineStep>().Property(item => item.Configuration));
            ConfigureDictionary(modelBuilder.Entity<BuildStepRun>().Property(item => item.Configuration));
        }

        private static void ConfigureDictionary(
            PropertyBuilder<Dictionary<string, string>> property) => property.HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<Dictionary<string, string>>(
                    value,
                    (JsonSerializerOptions?)null) ?? new Dictionary<string, string>());
    }
}
