using Lumina.BuildService.Data;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

public class PipelineEngine
{
    private readonly BuildDbContext _db;
    private readonly DockerBuildService _dockerBuild;
    private readonly ILogger<PipelineEngine> _logger;

    public PipelineEngine(BuildDbContext db, DockerBuildService dockerBuild, ILogger<PipelineEngine> logger)
    {
        _db = db;
        _dockerBuild = dockerBuild;
        _logger = logger;
    }

    public async Task<Pipeline> CreatePipelineAsync(Shared.DTOs.CreatePipelineRequest request, string createdBy)
    {
        var pipeline = new Pipeline
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description,
            Status = PipelineStatus.Active,
            CreatedBy = createdBy,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            GitRepoUrl = request.GitRepoUrl,
            GitBranch = request.GitBranch ?? "main",
            SpecPath = request.SpecPath,
            WebhookSecret = request.WebhookSecret,
            Steps = request.Steps.Select((s, i) => new PipelineStep
            {
                Id = Guid.NewGuid(),
                Type = s.Type,
                Name = s.Name,
                Order = s.Order,
                Status = StepStatus.Pending,
                Configuration = s.Configuration
            }).ToList()
        };

        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Pipeline {PipelineId} created: {Name}", pipeline.Id, pipeline.Name);
        return pipeline;
    }

    public async Task<BuildJob> TriggerBuildAsync(Guid pipelineId, Shared.DTOs.TriggerBuildRequest request)
    {
        var pipeline = await _db.Pipelines
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.Id == pipelineId);

        if (pipeline == null)
            throw new InvalidOperationException($"Pipeline {pipelineId} not found");

        var job = new BuildJob
        {
            Id = Guid.NewGuid(),
            PipelineId = pipelineId,
            Status = BuildStatus.Queued,
            SpecName = request.SpecName,
            SpecContent = request.SpecContent ?? string.Empty,
            SourceUrl = request.SourceUrl ?? string.Empty,
            TriggeredBy = request.TriggeredBy,
            CreatedAt = DateTime.UtcNow
        };

        _db.BuildJobs.Add(job);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Build job {JobId} queued for pipeline {PipelineId}", job.Id, pipelineId);

        // Find the build step in pipeline
        var buildStep = pipeline.Steps.FirstOrDefault(s => s.Type == StepType.Build);
        if (buildStep != null)
        {
            try
            {
                await _dockerBuild.StartBuildAsync(job, request.SpecContent, request.SourceUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Build start failed for job {JobId}, returning with Failed status", job.Id);
                // Job is already marked as Failed in DockerBuildService, just return it
            }
        }

        return job;
    }

    public async Task<List<Pipeline>> ListPipelinesAsync(int page = 1, int pageSize = 20)
    {
        return await _db.Pipelines
            .Include(p => p.Steps)
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<Pipeline?> GetPipelineAsync(Guid id)
    {
        return await _db.Pipelines
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<BuildJob?> GetBuildJobAsync(Guid id)
    {
        return await _db.BuildJobs
            .Include(b => b.Artifacts)
            .FirstOrDefaultAsync(b => b.Id == id);
    }

    public async Task<List<BuildJob>> ListBuildJobsAsync(int page = 1, int pageSize = 20)
    {
        return await _db.BuildJobs
            .OrderByDescending(b => b.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<List<BuildJob>> GetActiveBuildsAsync()
    {
        return await _db.BuildJobs
            .Where(b => b.Status == BuildStatus.Queued || b.Status == BuildStatus.Building)
            .OrderBy(b => b.CreatedAt)
            .ToListAsync();
    }
}