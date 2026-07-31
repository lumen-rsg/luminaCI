using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.DTOs;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
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
