using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.DTOs;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace Lumina.BuildService.Tests;

public class BuildProjectServiceTests
{
    [Fact]
    public async Task CreateAsync_NormalizesAndPersistsProject()
    {
        await using var db = NewContext(nameof(CreateAsync_NormalizesAndPersistsProject));
        var service = NewService(db);

        var project = await service.CreateAsync(
            Request() with { Name = "  Lumina packages  " },
            "cv2");

        Assert.Equal("Lumina packages", project.Name);
        Assert.Equal("https://github.com/lumina/packages.git", project.GitRepoUrl);
        Assert.Equal("main", project.GitBranch);
        Assert.Equal(".lumina/packages.yaml", project.ManifestPath);
        Assert.Equal("cv2", project.CreatedBy);
        Assert.True(project.IsActive);
        Assert.Equal(new string('s', 32), project.WebhookSecret);
        Assert.Equal("git-user", project.GitUsername);
        Assert.Equal("git-token", project.GitToken);
        Assert.Equal(1, await db.BuildProjects.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_RejectsDuplicateNameIgnoringCase()
    {
        await using var db = NewContext(nameof(CreateAsync_RejectsDuplicateNameIgnoringCase));
        var service = NewService(db);
        await service.CreateAsync(Request(), "cv2");

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.CreateAsync(Request() with { Name = "lumina packages" }, "cv2"));
    }

    [Theory]
    [InlineData("http://github.com/lumina/packages.git")]
    [InlineData("https://user:secret@github.com/lumina/packages.git")]
    [InlineData("https://github.com/lumina/packages.git?token=secret")]
    [InlineData("not-a-url")]
    public void Policy_RejectsUnsafeRepositoryUrl(string repositoryUrl)
    {
        Assert.Throws<ValidationException>(() =>
            BuildProjectPolicy.Validate(Request() with { GitRepoUrl = repositoryUrl }));
    }

    [Theory]
    [InlineData("../packages.yaml")]
    [InlineData("/packages.yaml")]
    [InlineData(".lumina/packages.json")]
    public void Policy_RejectsUnsafeManifestPath(string manifestPath)
    {
        Assert.Throws<ValidationException>(() =>
            BuildProjectPolicy.Validate(Request() with { ManifestPath = manifestPath }));
    }

    [Fact]
    public void Policy_RequiresStrongWebhookSecretAndCredentialPair()
    {
        Assert.Throws<ValidationException>(() =>
            BuildProjectPolicy.Validate(Request() with { WebhookSecret = "short" }));
        Assert.Throws<ValidationException>(() =>
            BuildProjectPolicy.Validate(Request() with { GitToken = null }));
    }

    [Fact]
    public async Task UpdateAsync_PreservesSecretsWhenTheyAreOmitted()
    {
        await using var db = NewContext(nameof(UpdateAsync_PreservesSecretsWhenTheyAreOmitted));
        var service = NewService(db);
        var project = await service.CreateAsync(Request(), "cv2");

        var updated = await service.UpdateAsync(project.Id, new UpdateBuildProjectRequest(
            "Lumina packages renamed",
            project.GitRepoUrl,
            "staging",
            project.ManifestPath,
            true,
            project.UpdatedAt));

        Assert.Equal("Lumina packages renamed", updated.Name);
        Assert.Equal("staging", updated.GitBranch);
        Assert.Equal(new string('s', 32), updated.WebhookSecret);
        Assert.Equal("git-user", updated.GitUsername);
        Assert.Equal("git-token", updated.GitToken);
    }

    [Fact]
    public async Task UpdateAsync_ClearsCredentialsAndRejectsStaleVersion()
    {
        await using var db = NewContext(nameof(UpdateAsync_ClearsCredentialsAndRejectsStaleVersion));
        var service = NewService(db);
        var project = await service.CreateAsync(Request(), "cv2");
        var staleVersion = project.UpdatedAt;

        var updated = await service.UpdateAsync(project.Id, new UpdateBuildProjectRequest(
            project.Name,
            project.GitRepoUrl,
            project.GitBranch,
            project.ManifestPath,
            true,
            project.UpdatedAt,
            ClearGitCredentials: true));

        Assert.Null(updated.GitUsername);
        Assert.Null(updated.GitToken);
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.UpdateAsync(project.Id, new UpdateBuildProjectRequest(
                project.Name,
                project.GitRepoUrl,
                project.GitBranch,
                project.ManifestPath,
                true,
                staleVersion)));
    }

    [Fact]
    public async Task BindPipelineAsync_PersistsExplicitPackageMapping()
    {
        await using var db = NewContext(nameof(BindPipelineAsync_PersistsExplicitPackageMapping));
        var service = NewService(db);
        var project = await service.CreateAsync(Request(), "cv2");
        var pipeline = AddPipeline(db, "firmware-pipeline");
        await db.SaveChangesAsync();

        var binding = await service.BindPipelineAsync(
            project.Id,
            new BindProjectPipelineRequest(pipeline.Id, "tegra-l4t-firmware"));

        Assert.Equal(project.Id, binding.BuildProjectId);
        Assert.Equal("tegra-l4t-firmware", binding.PackageId);
        var listed = await service.ListPipelineBindingsAsync(project.Id);
        Assert.Equal(pipeline.Id, Assert.Single(listed).Id);
    }

    [Fact]
    public async Task BindPipelineAsync_RejectsDuplicatePackageAndCrossProjectBinding()
    {
        await using var db = NewContext(
            nameof(BindPipelineAsync_RejectsDuplicatePackageAndCrossProjectBinding));
        var service = NewService(db);
        var firstProject = await service.CreateAsync(Request(), "cv2");
        var secondProject = await service.CreateAsync(
            Request() with { Name = "Another project" }, "cv2");
        var first = AddPipeline(db, "first");
        var second = AddPipeline(db, "second");
        await db.SaveChangesAsync();

        await service.BindPipelineAsync(firstProject.Id,
            new BindProjectPipelineRequest(first.Id, "driver"));
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.BindPipelineAsync(firstProject.Id,
                new BindProjectPipelineRequest(second.Id, "driver")));
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.BindPipelineAsync(secondProject.Id,
                new BindProjectPipelineRequest(first.Id, "driver")));
    }

    [Fact]
    public async Task DeleteAsync_RequiresBindingsToBeRemovedFirst()
    {
        await using var db = NewContext(nameof(DeleteAsync_RequiresBindingsToBeRemovedFirst));
        var service = NewService(db);
        var project = await service.CreateAsync(Request(), "cv2");
        var pipeline = AddPipeline(db, "driver");
        await db.SaveChangesAsync();
        await service.BindPipelineAsync(project.Id,
            new BindProjectPipelineRequest(pipeline.Id, "driver"));

        await Assert.ThrowsAsync<ConflictException>(() => service.DeleteAsync(project.Id));
        Assert.True(await service.UnbindPipelineAsync(project.Id, "driver"));
        Assert.True(await service.DeleteAsync(project.Id));
    }

    [Fact]
    public async Task DeleteAsync_PreservesWebhookDeliveryHistory()
    {
        await using var db = NewContext(nameof(DeleteAsync_PreservesWebhookDeliveryHistory));
        var service = NewService(db);
        var project = await service.CreateAsync(Request(), "cv2");
        db.ProjectWebhookDeliveries.Add(Delivery(project.Id, DateTime.UtcNow));
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ConflictException>(() => service.DeleteAsync(project.Id));
    }

    [Fact]
    public async Task DeliveryQueries_ArePagedAndConfinedToProject()
    {
        await using var db = NewContext(nameof(DeliveryQueries_ArePagedAndConfinedToProject));
        var service = NewService(db);
        var project = await service.CreateAsync(Request(), "cv2");
        var other = await service.CreateAsync(Request() with { Name = "Other project" }, "cv2");
        var older = Delivery(project.Id, DateTime.UtcNow.AddMinutes(-2));
        var newer = Delivery(project.Id, DateTime.UtcNow.AddMinutes(-1));
        var unrelated = Delivery(other.Id, DateTime.UtcNow);
        db.ProjectWebhookDeliveries.AddRange(older, newer, unrelated);
        await db.SaveChangesAsync();

        var (items, totalCount) = await service.ListDeliveriesAsync(project.Id, 1, 1);

        Assert.Equal(2, totalCount);
        Assert.Equal(newer.Id, Assert.Single(items).Id);
        Assert.Equal(older.Id, (await service.GetDeliveryAsync(project.Id, older.Id))!.Id);
        Assert.Null(await service.GetDeliveryAsync(project.Id, unrelated.Id));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.ListDeliveriesAsync(Guid.NewGuid(), 1, 20));
    }

    [Theory]
    [InlineData("Driver")]
    [InlineData("../driver")]
    [InlineData("")]
    public void Policy_RejectsInvalidPackageId(string packageId)
    {
        Assert.Throws<ValidationException>(() =>
            BuildProjectPolicy.NormalizePackageId(packageId));
    }

    private static CreateBuildProjectRequest Request() => new(
        "Lumina packages",
        "https://github.com/lumina/packages.git",
        "main",
        ".lumina/packages.yaml",
        new string('s', 32),
        "git-user",
        "git-token");

    private static BuildDbContext NewContext(string name)
    {
        var options = new DbContextOptionsBuilder<BuildDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new TestBuildDbContext(options);
    }

    private static BuildProjectService NewService(BuildDbContext db) =>
        new(db, NullLogger<BuildProjectService>.Instance);

    private static Pipeline AddPipeline(BuildDbContext db, string name)
    {
        var pipeline = new Pipeline
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = name,
            Status = PipelineStatus.Active,
            CreatedBy = "cv2",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            TargetDistribution = "fedora",
            TargetRelease = "44",
            TargetArchitecture = "aarch64",
            BuildProfile = "fedora-44-aarch64"
        };
        db.Pipelines.Add(pipeline);
        return pipeline;
    }

    private static ProjectWebhookDelivery Delivery(Guid projectId, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        BuildProjectId = projectId,
        ProviderDeliveryId = Guid.NewGuid().ToString("N"),
        RepositoryUrl = "https://github.com/lumina/packages.git",
        CommitSha = new string('a', 40),
        Branch = "main",
        ChangedPaths = ["package/file"],
        CreatedAt = createdAt,
        UpdatedAt = createdAt
    };

    private sealed class TestBuildDbContext(DbContextOptions<BuildDbContext> options)
        : BuildDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Pipeline>()
                .Property(pipeline => pipeline.Tags)
                .HasConversion(
                    value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                    value => JsonSerializer.Deserialize<List<string>>(
                        value, (JsonSerializerOptions?)null) ?? new List<string>());
            modelBuilder.Entity<PipelineStep>()
                .Property(step => step.Configuration)
                .HasConversion(
                    value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                    value => JsonSerializer.Deserialize<Dictionary<string, string>>(
                        value, (JsonSerializerOptions?)null) ?? new Dictionary<string, string>());
            modelBuilder.Entity<BuildStepRun>()
                .Property(step => step.Configuration)
                .HasConversion(
                    value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                    value => JsonSerializer.Deserialize<Dictionary<string, string>>(
                        value, (JsonSerializerOptions?)null) ?? new Dictionary<string, string>());
        }
    }
}
