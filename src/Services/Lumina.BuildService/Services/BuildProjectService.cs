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

    public async Task<(List<ProjectWebhookDelivery> Items, int TotalCount)> ListDeliveriesAsync(
        Guid projectId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (!await _db.BuildProjects.AnyAsync(project => project.Id == projectId, cancellationToken))
            throw new NotFoundException($"Build project {projectId} not found.");

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.ProjectWebhookDeliveries.AsNoTracking()
            .Where(delivery => delivery.BuildProjectId == projectId);
        var totalCount = await query.CountAsync(cancellationToken);
        var deliveries = await query
            .OrderByDescending(delivery => delivery.CreatedAt)
            .ThenByDescending(delivery => delivery.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (deliveries, totalCount);
    }

    public Task<ProjectWebhookDelivery?> GetDeliveryAsync(
        Guid projectId,
        Guid deliveryId,
        CancellationToken cancellationToken = default) =>
        _db.ProjectWebhookDeliveries.AsNoTracking().SingleOrDefaultAsync(
            delivery => delivery.BuildProjectId == projectId && delivery.Id == deliveryId,
            cancellationToken);

    public async Task<List<Pipeline>> ListPipelineBindingsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (!await _db.BuildProjects.AnyAsync(project => project.Id == projectId, cancellationToken))
            throw new NotFoundException($"Build project {projectId} not found.");
        return await _db.Pipelines.AsNoTracking()
            .Where(pipeline => pipeline.BuildProjectId == projectId)
            .OrderBy(pipeline => pipeline.PackageId)
            .ToListAsync(cancellationToken);
    }

    public async Task<Pipeline> BindPipelineAsync(
        Guid projectId,
        BindProjectPipelineRequest request,
        CancellationToken cancellationToken = default)
    {
        var packageId = BuildProjectPolicy.NormalizePackageId(request.PackageId);
        if (!await _db.BuildProjects.AnyAsync(project => project.Id == projectId, cancellationToken))
            throw new NotFoundException($"Build project {projectId} not found.");

        var pipeline = await _db.Pipelines.SingleOrDefaultAsync(
            item => item.Id == request.PipelineId,
            cancellationToken);
        if (pipeline is null)
            throw new NotFoundException($"Pipeline {request.PipelineId} not found.");
        if (pipeline.BuildProjectId.HasValue && pipeline.BuildProjectId != projectId)
        {
            throw new ConflictException(
                $"Pipeline {pipeline.Id} is already bound to another build project.");
        }

        var existing = await _db.Pipelines.SingleOrDefaultAsync(
            item => item.BuildProjectId == projectId && item.PackageId == packageId,
            cancellationToken);
        if (existing is not null && existing.Id != pipeline.Id)
        {
            throw new ConflictException(
                $"Package '{packageId}' is already bound to pipeline {existing.Id}.");
        }

        pipeline.BuildProjectId = projectId;
        pipeline.PackageId = packageId;
        pipeline.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new ConflictException(
                $"Package '{packageId}' is already bound to another pipeline.", exception);
        }

        _logger.LogInformation(
            "Pipeline {PipelineId} bound to project {ProjectId} package {PackageId}",
            pipeline.Id, projectId, packageId);
        return pipeline;
    }

    public async Task<bool> UnbindPipelineAsync(
        Guid projectId,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        var normalizedPackageId = BuildProjectPolicy.NormalizePackageId(packageId);
        if (!await _db.BuildProjects.AnyAsync(project => project.Id == projectId, cancellationToken))
            throw new NotFoundException($"Build project {projectId} not found.");

        var pipeline = await _db.Pipelines.SingleOrDefaultAsync(
            item => item.BuildProjectId == projectId && item.PackageId == normalizedPackageId,
            cancellationToken);
        if (pipeline is null)
            return false;

        pipeline.BuildProjectId = null;
        pipeline.PackageId = null;
        pipeline.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Pipeline {PipelineId} unbound from project {ProjectId}", pipeline.Id, projectId);
        return true;
    }

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

        if (await _db.Pipelines.AnyAsync(
                pipeline => pipeline.BuildProjectId == id,
                cancellationToken))
        {
            throw new ConflictException(
                "A build project with bound pipelines cannot be deleted. Unbind them first.");
        }

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
