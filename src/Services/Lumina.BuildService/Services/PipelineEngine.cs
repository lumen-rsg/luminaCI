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
            BuildImage = request.BuildImage,
            GitUsername = request.GitUsername,
            GitToken = request.GitToken,
            Tags = request.Tags ?? new List<string>(),
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

        // Auto-populate SourceUrl from pipeline git config if not provided
        var sourceUrl = request.SourceUrl;
        var specContent = request.SpecContent;
        var specName = request.SpecName;

        if (string.IsNullOrWhiteSpace(sourceUrl) && !string.IsNullOrWhiteSpace(pipeline.GitRepoUrl))
        {
            var branch = pipeline.GitBranch ?? "main";
            var specPath = pipeline.SpecPath ?? $"{pipeline.Name}.spec";
            sourceUrl = $"git://{pipeline.GitRepoUrl}#branch={branch}&specPath={specPath}";
            _logger.LogInformation("Auto-populated SourceUrl from pipeline config: {SourceUrl}", sourceUrl);
        }

        // Auto-determine spec name from pipeline config
        if (string.IsNullOrWhiteSpace(specName) || specName == "package.spec")
        {
            specName = !string.IsNullOrEmpty(pipeline.SpecPath)
                ? Path.GetFileName(pipeline.SpecPath)
                : $"{pipeline.Name}.spec";
        }

        // If source is from git, spec content will be read from the cloned repo
        if (!string.IsNullOrWhiteSpace(sourceUrl) && sourceUrl.StartsWith("git://") && string.IsNullOrWhiteSpace(specContent))
        {
            specContent = string.Empty; // Will be read from cloned repo
        }

        var job = new BuildJob
        {
            Id = Guid.NewGuid(),
            PipelineId = pipelineId,
            Status = BuildStatus.Queued,
            SpecName = specName,
            SpecContent = specContent ?? string.Empty,
            SourceUrl = sourceUrl ?? string.Empty,
            TriggeredBy = request.TriggeredBy,
            CreatedAt = DateTime.UtcNow,
            // Git metadata from webhook or auto-build
            CommitSha = request.CommitSha,
            Branch = request.Branch ?? pipeline.GitBranch,
            CommitMessage = request.CommitMessage,
            CommitAuthor = request.CommitAuthor
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
                await _dockerBuild.StartBuildAsync(job, specContent, sourceUrl, pipeline.BuildImage,
                    pipeline.GitUsername, pipeline.GitToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Build start failed for job {JobId}, returning with Failed status", job.Id);
                // Job is already marked as Failed in DockerBuildService, just return it
            }
        }

        return job;
    }

    /// <summary>
    /// Trigger a build using only the pipeline's configured git settings.
    /// No spec content or source URL needed — everything comes from the git repo.
    /// </summary>
    public async Task<BuildJob> TriggerAutoBuildAsync(Guid pipelineId, string triggeredBy)
    {
        var pipeline = await _db.Pipelines
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.Id == pipelineId);

        if (pipeline == null)
            throw new InvalidOperationException($"Pipeline {pipelineId} not found");

        if (string.IsNullOrWhiteSpace(pipeline.GitRepoUrl))
            throw new InvalidOperationException($"Pipeline {pipelineId} has no Git repository URL configured. Cannot auto-build.");

        var branch = pipeline.GitBranch ?? "main";
        var specPath = pipeline.SpecPath ?? $"{pipeline.Name}.spec";
        var specName = Path.GetFileName(specPath);

        var request = new Shared.DTOs.TriggerBuildRequest(
            specName,
            string.Empty,  // Spec will be read from cloned repo
            $"git://{pipeline.GitRepoUrl}#branch={branch}&specPath={specPath}",
            triggeredBy
        );

        return await TriggerBuildAsync(pipelineId, request);
    }

    /// <summary>
    /// Trigger a build using pre-fetched sources from the SourceService.
    /// This is used when sources are downloaded by the SourceService (git, http, ftp, rsync, svn, hg).
    /// The SourceService downloads sources to MinIO, then we download them locally and mount into the build container.
    /// </summary>
    public async Task<BuildJob> TriggerBuildFromConfigAsync(
        string packageName, string sourceDir, string specContent, string specName,
        string? buildImage = null, string triggeredBy = "source-service")
    {
        // Find or create a pipeline for this package
        var pipeline = await _db.Pipelines
            .FirstOrDefaultAsync(p => p.Name == packageName);

        if (pipeline == null)
        {
            pipeline = new Pipeline
            {
                Id = Guid.NewGuid(),
                Name = packageName,
                Description = $"Auto-created pipeline for {packageName} (from conf.ini)",
                Status = PipelineStatus.Active,
                CreatedBy = triggeredBy,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SpecPath = specName,
                BuildImage = buildImage,
                Steps = new List<PipelineStep>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Type = StepType.Build,
                        Name = "Build RPM",
                        Order = 1,
                        Status = StepStatus.Pending,
                        Configuration = new Dictionary<string, string>
                        {
                            { "sourceType", "pre-fetched" }
                        }
                    }
                }
            };

            _db.Pipelines.Add(pipeline);
            await _db.SaveChangesAsync();
            _logger.LogInformation("Auto-created pipeline {PipelineId} for package {Package}", pipeline.Id, packageName);
        }

        var job = new BuildJob
        {
            Id = Guid.NewGuid(),
            PipelineId = pipeline.Id,
            Status = BuildStatus.Queued,
            SpecName = specName,
            SpecContent = specContent,
            SourceUrl = $"pre-fetched://{packageName}",
            TriggeredBy = triggeredBy,
            CreatedAt = DateTime.UtcNow
        };

        _db.BuildJobs.Add(job);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Build job {JobId} queued for package {Package} with pre-fetched sources", job.Id, packageName);

        var buildStep = pipeline.Steps.FirstOrDefault(s => s.Type == StepType.Build);
        if (buildStep != null)
        {
            try
            {
                await _dockerBuild.StartBuildAsync(
                    job, specContent, null, buildImage ?? pipeline.BuildImage,
                    sourceDir: sourceDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Build start failed for job {JobId}", job.Id);
            }
        }

        return job;
    }

    public async Task<(List<Pipeline> Items, int TotalCount)> ListPipelinesAsync(int page = 1, int pageSize = 20)
    {
        var totalCount = await _db.Pipelines.CountAsync();
        var items = await _db.Pipelines
            .Include(p => p.Steps)
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return (items, totalCount);
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

    public async Task<(List<BuildJob> Items, int TotalCount)> ListBuildJobsAsync(int page = 1, int pageSize = 20)
    {
        var totalCount = await _db.BuildJobs.CountAsync();
        var items = await _db.BuildJobs
            .OrderByDescending(b => b.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return (items, totalCount);
    }

    public async Task<List<BuildJob>> GetActiveBuildsAsync()
    {
        return await _db.BuildJobs
            .Where(b => b.Status == BuildStatus.Queued || b.Status == BuildStatus.Building)
            .OrderBy(b => b.CreatedAt)
            .ToListAsync();
    }
}