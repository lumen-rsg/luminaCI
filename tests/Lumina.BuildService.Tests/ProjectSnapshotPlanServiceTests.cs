using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumina.BuildService.Data;
using Lumina.BuildService.Services.PackageGraph;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class ProjectSnapshotPlanServiceTests
{
    [Fact]
    public async Task ProcessAsync_PersistsDependencyOrderedDryRunPlan()
    {
        var archive = CreateArchive(Manifest());
        await using var db = CreateDb(nameof(ProcessAsync_PersistsDependencyOrderedDryRunPlan));
        var delivery = AddDelivery(db, archive, ["firmware/blob.bin"]);
        await db.SaveChangesAsync();

        await new ProjectSnapshotPlanService(db, new MemorySnapshotProvider(archive))
            .ProcessAsync(delivery.Id, default);

        Assert.Equal(ProjectWebhookStatus.PlanReady, delivery.Status);
        Assert.Null(delivery.FailureCode);
        Assert.Equal(Hash(Manifest()), delivery.ManifestSha256);
        var plan = JsonSerializer.Deserialize<ProjectDispatchPlan>(delivery.DispatchPlanJson!, JsonOptions)!;
        Assert.Equal(2, plan.Stages.Count);
        Assert.Equal("firmware", Assert.Single(plan.Stages[0].Targets).PackageId);
        Assert.Equal("driver", Assert.Single(plan.Stages[1].Targets).PackageId);
    }

    [Fact]
    public async Task ProcessAsync_PersistsIgnoredPlanForUnrelatedChanges()
    {
        var archive = CreateArchive(Manifest());
        await using var db = CreateDb(nameof(ProcessAsync_PersistsIgnoredPlanForUnrelatedChanges));
        var delivery = AddDelivery(db, archive, ["README.md"]);
        await db.SaveChangesAsync();

        await new ProjectSnapshotPlanService(db, new MemorySnapshotProvider(archive))
            .ProcessAsync(delivery.Id, default);

        Assert.Equal(ProjectWebhookStatus.Ignored, delivery.Status);
        var plan = JsonSerializer.Deserialize<ProjectDispatchPlan>(delivery.DispatchPlanJson!, JsonOptions)!;
        Assert.Empty(plan.Stages);
    }

    [Fact]
    public async Task ProcessAsync_FailsClosedForInvalidManifest()
    {
        var archive = CreateArchive("version: 1\npackages: {}\n");
        await using var db = CreateDb(nameof(ProcessAsync_FailsClosedForInvalidManifest));
        var delivery = AddDelivery(db, archive, ["firmware/blob.bin"]);
        await db.SaveChangesAsync();

        await new ProjectSnapshotPlanService(db, new MemorySnapshotProvider(archive))
            .ProcessAsync(delivery.Id, default);

        Assert.Equal(ProjectWebhookStatus.Failed, delivery.Status);
        Assert.Equal("snapshot-plan-invalid", delivery.FailureCode);
        Assert.Null(delivery.DispatchPlanJson);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static ProjectWebhookDelivery AddDelivery(
        BuildDbContext db,
        byte[] archive,
        List<string> changedPaths)
    {
        var projectId = Guid.NewGuid();
        var hash = Hash(archive);
        var project = new BuildProject
        {
            Id = projectId,
            Name = "Lumina",
            GitRepoUrl = "https://github.com/lumina/packages.git",
            GitBranch = "main",
            ManifestPath = ".lumina/packages.yaml",
            WebhookSecret = new string('s', 32),
            CreatedBy = "cv2"
        };
        project.Pipelines.Add(Pipeline(projectId, "firmware", "specs/firmware.spec"));
        project.Pipelines.Add(Pipeline(projectId, "driver", "specs/driver.spec"));
        var delivery = new ProjectWebhookDelivery
        {
            Id = Guid.NewGuid(),
            BuildProjectId = projectId,
            BuildProject = project,
            ProviderDeliveryId = Guid.NewGuid().ToString("N"),
            RepositoryUrl = project.GitRepoUrl,
            CommitSha = new string('a', 40),
            Branch = "main",
            ChangedPaths = changedPaths,
            Status = ProjectWebhookStatus.SnapshotReady,
            SnapshotStoragePath = $"project-{projectId:N}/{hash}/project-{projectId:N}-sources.tar.gz",
            SnapshotSha256 = hash,
            SnapshotFileSize = archive.Length
        };
        db.BuildProjects.Add(project);
        db.ProjectWebhookDeliveries.Add(delivery);
        return delivery;
    }

    private static Pipeline Pipeline(Guid projectId, string packageId, string specPath) => new()
    {
        Id = Guid.NewGuid(),
        BuildProjectId = projectId,
        PackageId = packageId,
        Name = packageId,
        Description = packageId,
        Status = PipelineStatus.Active,
        CreatedBy = "cv2",
        SpecPath = specPath,
        BuildProfile = "fedora-44-aarch64"
    };

    private static string Manifest() => """
        version: 1
        packages:
          firmware:
            spec: specs/firmware.spec
            paths: [firmware/**]
            targets: [fedora-44-aarch64]
          driver:
            spec: specs/driver.spec
            paths: [driver/**]
            targets: [fedora-44-aarch64]
            depends_on: [firmware]
        """;

    private static byte[] CreateArchive(string manifest)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "snapshot/.lumina/packages.yaml")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(manifest))
            });
        }
        return output.ToArray();
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));

    private static BuildDbContext CreateDb(string name)
    {
        var options = new DbContextOptionsBuilder<BuildDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new TestBuildDbContext(options);
    }

    private sealed class MemorySnapshotProvider(byte[] archive) : IRepositorySnapshotStreamProvider
    {
        public Task<RepositorySnapshotStream> OpenAsync(
            Guid projectId,
            string storagePath,
            string expectedSha256,
            long expectedSize,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RepositorySnapshotStream(new MemoryStream(archive)));
    }

    private sealed class TestBuildDbContext(DbContextOptions<BuildDbContext> options)
        : BuildDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<PipelineStep>().Ignore(step => step.Configuration);
            modelBuilder.Entity<BuildStepRun>().Ignore(step => step.Configuration);
        }
    }
}
