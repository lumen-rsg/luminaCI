using Lumina.BuildService.Data;
using Lumina.Shared.DTOs;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

public sealed class BuildProjectService
{
    private readonly BuildDbContext _db;
    private readonly ILogger<BuildProjectService> _logger;

    public BuildProjectService(BuildDbContext db, ILogger<BuildProjectService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<(List<BuildProject> Items, int TotalCount)> ListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.BuildProjects.AsNoTracking();
        var totalCount = await query.CountAsync(cancellationToken);
        var projects = await query
            .OrderBy(project => project.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (projects, totalCount);
    }

    public Task<BuildProject?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        _db.BuildProjects.AsNoTracking().SingleOrDefaultAsync(
            project => project.Id == id,
            cancellationToken);

    public async Task<BuildProject> CreateAsync(
        CreateBuildProjectRequest request,
        string createdBy,
        CancellationToken cancellationToken = default)
    {
        var normalized = BuildProjectPolicy.Validate(request);
        if (await NameExistsAsync(normalized.Name, null, cancellationToken))
            throw new ConflictException($"A build project named '{normalized.Name}' already exists.");

        var now = DateTime.UtcNow;
        var project = new BuildProject
        {
            Id = Guid.NewGuid(),
            Name = normalized.Name,
            GitRepoUrl = normalized.GitRepoUrl,
            GitBranch = normalized.GitBranch,
            ManifestPath = normalized.ManifestPath,
            WebhookSecret = request.WebhookSecret,
            GitUsername = normalized.GitUsername,
            GitToken = normalized.GitToken,
            IsActive = true,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? "system" : createdBy,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.BuildProjects.Add(project);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new ConflictException(
                $"A build project named '{normalized.Name}' already exists.", exception);
        }

        _logger.LogInformation("Build project {ProjectId} created", project.Id);
        return project;
    }

    public async Task<BuildProject> UpdateAsync(
        Guid id,
        UpdateBuildProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = BuildProjectPolicy.Validate(request);
        var project = await _db.BuildProjects.SingleOrDefaultAsync(
            item => item.Id == id,
            cancellationToken);
        if (project is null)
            throw new NotFoundException($"Build project {id} not found.");
        if (project.UpdatedAt != request.ExpectedUpdatedAt)
        {
            throw new ConflictException(
                "The build project was modified by another user. Reload it before saving.");
        }
        if (await NameExistsAsync(normalized.Name, id, cancellationToken))
            throw new ConflictException($"A build project named '{normalized.Name}' already exists.");

        project.Name = normalized.Name;
        project.GitRepoUrl = normalized.GitRepoUrl;
        project.GitBranch = normalized.GitBranch;
        project.ManifestPath = normalized.ManifestPath;
        project.IsActive = request.IsActive;
        project.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.WebhookSecret))
            project.WebhookSecret = request.WebhookSecret;
        if (request.ClearGitCredentials)
        {
            project.GitUsername = null;
            project.GitToken = null;
        }
        else if (normalized.GitUsername is not null)
        {
            project.GitUsername = normalized.GitUsername;
            project.GitToken = normalized.GitToken;
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConflictException(
                "The build project was modified by another user. Reload it before saving.", exception);
        }
        catch (DbUpdateException exception)
        {
            throw new ConflictException(
                $"A build project named '{normalized.Name}' already exists.", exception);
        }

        _logger.LogInformation("Build project {ProjectId} updated", project.Id);
        return project;
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var project = await _db.BuildProjects.SingleOrDefaultAsync(
            item => item.Id == id,
            cancellationToken);
        if (project is null)
            return false;

        _db.BuildProjects.Remove(project);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Build project {ProjectId} deleted", project.Id);
        return true;
    }

    private Task<bool> NameExistsAsync(
        string name,
        Guid? excludingId,
        CancellationToken cancellationToken) =>
        _db.BuildProjects.AnyAsync(
            project => project.Name.ToLower() == name.ToLower() &&
                       (!excludingId.HasValue || project.Id != excludingId.Value),
            cancellationToken);
}
