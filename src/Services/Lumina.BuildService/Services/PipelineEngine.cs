using Lumina.BuildService.Data;
using Lumina.Shared.Errors;
using Lumina.Shared.Extensions;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

public class PipelineEngine
{
    private readonly BuildDbContext _db;
    private readonly IBuildLauncher _buildLauncher;
    private readonly ILogger<PipelineEngine> _logger;
    private readonly RedisCacheService _cache;
    private readonly ISigningKeyGate _signingKeyGate;

    public PipelineEngine(BuildDbContext db, IBuildLauncher buildLauncher, ILogger<PipelineEngine> logger, RedisCacheService cache, ISigningKeyGate signingKeyGate)
    {
        _db = db;
        _buildLauncher = buildLauncher;
        _logger = logger;
        _cache = cache;
        _signingKeyGate = signingKeyGate;
    }

    /// <summary>
    /// Verifies an active PGP signing key exists before allowing a build of a
    /// pipeline that declares a <see cref="StepType.Sign"/> step. Without an
    /// active key the build would silently degrade to an unsigned artifact that
    /// then cannot be published — failing the trigger early gives the operator a
    /// clear, recoverable error instead of a wasted build.
    /// </summary>
    private Task RequireActiveSigningKeyAsync() => _signingKeyGate.RequireActiveKeyAsync();

    public async Task<Pipeline> CreatePipelineAsync(Shared.DTOs.CreatePipelineRequest request, string createdBy)
    {
        // Fail-closed at creation: every pipeline must carry a non-empty webhook
        // secret. The webhook endpoint (WebhooksController) rejects any pipeline
        // without one, so allowing a pipeline to be created without a secret
        // would produce a pipeline whose webhook URL is permanently dead — and,
        // worse, would have been trivially triggerable by anyone before the
        // fail-closed handler gate landed. Requiring it here gives the operator
        // a clear, early error instead of a silent foot-gun.
        if (string.IsNullOrWhiteSpace(request.WebhookSecret))
        {
            throw new ValidationException(
                "A non-empty WebhookSecret is required — pipelines without a webhook secret cannot be triggered securely.");
        }

        PipelineDefinitionValidator.Validate(request.Steps);
        var target = BuildTargetPolicy.Resolve(
            request.TargetDistribution,
            request.TargetRelease,
            request.TargetArchitecture,
            request.BuildProfile);

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
            TriggerPaths = WebhookPathFilter.Normalize(request.TriggerPaths, request.SpecPath),
            WebhookSecret = request.WebhookSecret,
            BuildImage = request.BuildImage,
            TargetDistribution = target.Distribution,
            TargetRelease = target.Release,
            TargetArchitecture = target.Architecture,
            BuildProfile = target.Profile,
            GitUsername = request.GitUsername,
            GitToken = request.GitToken,
            SpecContent = request.SpecContent,
            Tags = request.Tags ?? new List<string>(),
            Steps = request.Steps.OrderBy(s => s.Order).Select(s => new PipelineStep
            {
                Id = Guid.NewGuid(),
                Type = s.Type,
                Name = s.Name,
                Order = s.Order,
                Configuration = s.Configuration ?? new Dictionary<string, string>()
            }).ToList()
        };

        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync();

        // Invalidate pipeline list cache so new pipeline appears immediately
        await InvalidatePipelineCacheAsync();

        _logger.LogInformation("Pipeline {PipelineId} created: {Name}", pipeline.Id, pipeline.Name);
        return pipeline;
    }

    public async Task<BuildJob> TriggerBuildAsync(Guid pipelineId, Shared.DTOs.TriggerBuildRequest request)
    {
        var pipeline = await _db.Pipelines
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.Id == pipelineId);

        if (pipeline == null)
            throw new NotFoundException($"Pipeline {pipelineId} not found");

        if (pipeline.Status != PipelineStatus.Active)
        {
            throw new ValidationException(
                $"Pipeline {pipelineId} is {pipeline.Status} and cannot be triggered. Activate it first.");
        }

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existingJob = await _db.BuildJobs
                .Include(job => job.Artifacts)
                .Include(job => job.StepRuns)
                .SingleOrDefaultAsync(job =>
                    job.PipelineId == pipelineId
                    && job.IdempotencyKey == request.IdempotencyKey);
            if (existingJob is not null)
            {
                _logger.LogInformation(
                    "Returning existing build {JobId} for idempotency key {IdempotencyKey}",
                    existingJob.Id, request.IdempotencyKey);
                return existingJob;
            }
        }

        PipelineDefinitionValidator.Validate(pipeline.Steps);
        var target = BuildTargetPolicy.Resolve(
            pipeline.TargetDistribution,
            pipeline.TargetRelease,
            pipeline.TargetArchitecture,
            pipeline.BuildProfile);

        // Fail-closed: a pipeline that declares a Sign step must have an active
        // PGP key before any build starts, otherwise the artifact would be built
        // and scanned only to fail publication later. TriggerAuto funnels
        // through here too, so this single gate covers both manual and webhook
        // entry points.
        if (pipeline.Steps.Any(s => s.Type == StepType.Sign))
        {
            await RequireActiveSigningKeyAsync();
        }

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
            IdempotencyKey = request.IdempotencyKey,
            CreatedAt = DateTime.UtcNow,
            // Git metadata from webhook or auto-build
            CommitSha = request.CommitSha,
            Branch = request.Branch ?? pipeline.GitBranch,
            CommitMessage = request.CommitMessage,
            CommitAuthor = request.CommitAuthor,
            TargetDistribution = target.Distribution,
            TargetRelease = target.Release,
            TargetArchitecture = target.Architecture,
            BuildProfile = target.Profile,
            ExecutionBackend = _buildLauncher.Backend,
            StepRuns = pipeline.Steps
                .OrderBy(step => step.Order)
                .Select(step => new BuildStepRun
                {
                    Id = Guid.NewGuid(),
                    PipelineStepId = step.Id,
                    Type = step.Type,
                    Name = step.Name,
                    Order = step.Order,
                    Configuration = new Dictionary<string, string>(step.Configuration)
                })
                .ToList()
        };

        var buildRun = job.StepRuns.Single(step => step.Type == StepType.Build);
        buildRun.Status = StepStatus.Running;
        buildRun.StartedAt = DateTime.UtcNow;

        _db.BuildJobs.Add(job);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            // A concurrent delivery may have inserted the same webhook key after
            // our pre-check. The unique index is authoritative; return its job.
            _db.ChangeTracker.Clear();
            var winner = await _db.BuildJobs
                .Include(item => item.Artifacts)
                .Include(item => item.StepRuns)
                .SingleOrDefaultAsync(item =>
                    item.PipelineId == pipelineId
                    && item.IdempotencyKey == request.IdempotencyKey);
            if (winner is not null)
                return winner;
            throw;
        }

        _logger.LogInformation("Build job {JobId} queued for pipeline {PipelineId}", job.Id, pipelineId);

        try
        {
            // Pipeline-level extra sources directory
            var pipelineExtraDir = $"/opt/lumina/extra-sources/pipelines/{pipelineId}";
            var pipelineExtraExists = Directory.Exists(pipelineExtraDir) && Directory.GetFiles(pipelineExtraDir, "*", SearchOption.AllDirectories).Length > 0;

            await _buildLauncher.StartBuildAsync(job, specContent, sourceUrl, pipeline.BuildImage,
                pipeline.GitUsername, pipeline.GitToken,
                extraSourcesPipelineDir: pipelineExtraExists ? pipelineExtraDir : null);
        }
        catch (Exception ex)
        {
            job.Status = BuildStatus.Failed;
            job.CompletedAt = DateTime.UtcNow;
            buildRun.Status = StepStatus.Failed;
            buildRun.Error = ex.Message.Length > 2048 ? ex.Message[..2048] : ex.Message;
            buildRun.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            _logger.LogWarning(ex, "Build start failed for job {JobId}", job.Id);
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
            throw new NotFoundException($"Pipeline {pipelineId} not found");

        if (pipeline.Status != PipelineStatus.Active)
        {
            throw new ValidationException(
                $"Pipeline {pipelineId} is {pipeline.Status} and cannot be triggered. Activate it first.");
        }

        if (string.IsNullOrWhiteSpace(pipeline.GitRepoUrl))
            throw new ValidationException($"Pipeline {pipelineId} has no Git repository URL configured. Cannot auto-build.");

        var branch = pipeline.GitBranch ?? "main";
        var specPath = pipeline.SpecPath ?? $"{pipeline.Name}.spec";
        var specName = Path.GetFileName(specPath);

        var request = new Shared.DTOs.TriggerBuildRequest(
            specName,
            pipeline.SpecContent ?? string.Empty,
            $"git://{pipeline.GitRepoUrl}#branch={branch}&specPath={specPath}",
            triggeredBy
        );

        return await TriggerBuildAsync(pipelineId, request);
    }

    public async Task<(List<Pipeline> Items, int TotalCount)> ListPipelinesAsync(int page = 1, int pageSize = 20, string? search = null)
    {
        // Not cached: this returns EF Core entities with navigation properties
        // (Pipeline.Steps), which System.Text.Json cannot round-trip. The
        // RedisCacheService contract is "DTOs only" and now rejects non-round-
        // trippable values; to cache lists here, project to a DTO first.
        var query = _db.Pipelines.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(term) ||
                                     p.Description.ToLower().Contains(term));
        }
        var totalCount = await query.CountAsync();
        var items = await query
            .Include(p => p.Steps)
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return (items, totalCount);
    }

    public async Task<Pipeline?> GetPipelineAsync(Guid id)
    {
        // Not cached: EF entity with navigation properties — see RedisCacheService
        // contract (cache DTOs only). Project to a DTO first to enable caching.
        return await _db.Pipelines
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<BuildJob?> GetBuildJobAsync(Guid id)
    {
        // Not cached: EF entity with navigation properties — see RedisCacheService contract.
        return await _db.BuildJobs
            .Include(b => b.Artifacts)
            .Include(b => b.StepRuns)
            .FirstOrDefaultAsync(b => b.Id == id);
    }

    public async Task<(List<BuildJob> Items, int TotalCount)> ListBuildJobsAsync(
        int page = 1, int pageSize = 20, BuildStatus? status = null)
    {
        // Not cached: EF entities — see ListPipelinesAsync / RedisCacheService contract.
        var query = _db.BuildJobs.AsQueryable();
        if (status.HasValue)
            query = query.Where(b => b.Status == status.Value);
        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(b => b.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return (items, totalCount);
    }

    public async Task<(int Total, int Successful, int Failed)> GetBuildStatsAsync()
    {
        var counts = await _db.BuildJobs
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Successful = g.Count(b => b.Status == BuildStatus.Success),
                Failed = g.Count(b => b.Status == BuildStatus.Failed)
            })
            .SingleOrDefaultAsync();
        return counts is null ? (0, 0, 0) : (counts.Total, counts.Successful, counts.Failed);
    }

    public async Task<List<BuildJob>> GetActiveBuildsAsync()
    {
        return await _db.BuildJobs
            .Where(b => b.Status == BuildStatus.Queued || b.Status == BuildStatus.Building)
            .OrderBy(b => b.CreatedAt)
            .ToListAsync();
    }

    public async Task<BuildJob?> GetBuildJobByArtifactIdAsync(Guid artifactId)
    {
        return await _db.BuildJobs
            .Include(b => b.Artifacts)
            .FirstOrDefaultAsync(b => b.Artifacts.Any(a => a.Id == artifactId));
    }

    public async Task<Pipeline> UpdatePipelineAsync(Guid id, Shared.DTOs.UpdatePipelineRequest request)
    {
        var pipeline = await _db.Pipelines.Include(p => p.Steps).FirstOrDefaultAsync(p => p.Id == id);
        if (pipeline == null)
            throw new NotFoundException($"Pipeline {id} not found");

        if (!request.ExpectedUpdatedAt.HasValue)
            throw new ValidationException("The pipeline version is required");

        if (pipeline.UpdatedAt != request.ExpectedUpdatedAt.Value)
            throw new ConflictException("The pipeline was modified by another user. Reload it before saving.");

        PipelineDefinitionValidator.Validate(request.Steps);
        var target = BuildTargetPolicy.Resolve(
            request.TargetDistribution,
            request.TargetRelease,
            request.TargetArchitecture,
            request.BuildProfile);

        pipeline.Name = request.Name;
        pipeline.Description = request.Description;
        pipeline.Tags = request.Tags;
        pipeline.UpdatedAt = DateTime.UtcNow;

        // Update git fields if provided
        if (request.GitRepoUrl != null) pipeline.GitRepoUrl = request.GitRepoUrl;
        if (request.GitBranch != null) pipeline.GitBranch = request.GitBranch;
        if (request.SpecPath != null) pipeline.SpecPath = request.SpecPath;
        pipeline.TriggerPaths = WebhookPathFilter.Normalize(
            request.TriggerPaths ?? pipeline.TriggerPaths,
            request.SpecPath ?? pipeline.SpecPath);
        if (request.BuildImage != null) pipeline.BuildImage = request.BuildImage;
        pipeline.TargetDistribution = target.Distribution;
        pipeline.TargetRelease = target.Release;
        pipeline.TargetArchitecture = target.Architecture;
        pipeline.BuildProfile = target.Profile;
        if (request.GitUsername != null) pipeline.GitUsername = request.GitUsername;
        if (request.GitToken != null) pipeline.GitToken = request.GitToken;
        if (request.SpecContent != null) pipeline.SpecContent = request.SpecContent;

        // Replace steps
        _db.PipelineSteps.RemoveRange(pipeline.Steps);
        pipeline.Steps = request.Steps.OrderBy(s => s.Order).Select(s => new PipelineStep
        {
            Id = Guid.NewGuid(),
            PipelineId = pipeline.Id,
            Type = s.Type,
            Name = s.Name,
            Order = s.Order,
            Configuration = s.Configuration ?? new Dictionary<string, string>()
        }).ToList();

        _db.PipelineSteps.AddRange(pipeline.Steps);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConflictException("The pipeline was modified by another user. Reload it before saving.", ex);
        }

        // Invalidate cache so changes appear immediately
        await InvalidatePipelineCacheAsync(id);

        _logger.LogInformation("Pipeline {PipelineId} updated", pipeline.Id);
        return pipeline;
    }

    public async Task<bool> DeletePipelineAsync(Guid id)
    {
        var pipeline = await _db.Pipelines.Include(p => p.Steps).FirstOrDefaultAsync(p => p.Id == id);
        if (pipeline == null) return false;

        // Check for active builds
        var activeBuilds = await _db.BuildJobs
            .Where(b => b.PipelineId == id && (b.Status == BuildStatus.Queued || b.Status == BuildStatus.Building))
            .CountAsync();
        if (activeBuilds > 0)
            throw new ConflictException($"Cannot delete pipeline {id}: {activeBuilds} active build(s) running");

        _db.PipelineSteps.RemoveRange(pipeline.Steps);
        _db.Pipelines.Remove(pipeline);
        await _db.SaveChangesAsync();

        // Invalidate cache so deletion appears immediately
        await InvalidatePipelineCacheAsync(id);

        _logger.LogInformation("Pipeline {PipelineId} deleted", id);
        return true;
    }

    public async Task<int> ClearQueuedBuildsAsync()
    {
        var queuedJobs = await _db.BuildJobs
            .Where(b => b.Status == BuildStatus.Queued)
            .Include(b => b.StepRuns)
            .ToListAsync();

        foreach (var job in queuedJobs)
        {
            job.Status = BuildStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            foreach (var step in job.StepRuns.Where(step =>
                         step.Status is StepStatus.Pending or StepStatus.Running))
            {
                step.Status = StepStatus.Skipped;
                step.CompletedAt = DateTime.UtcNow;
                step.Error = "Removed from the queued build list.";
            }
        }

        _db.BuildJobs.UpdateRange(queuedJobs);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Cleared {Count} queued builds", queuedJobs.Count);
        return queuedJobs.Count;
    }

    /// <summary>
    /// Invalidate pipeline-related cache entries after mutations (create, update, delete).
    /// Removes both individual pipeline cache and all paginated list caches.
    /// </summary>
    private async Task InvalidatePipelineCacheAsync(Guid? pipelineId = null)
    {
        try
        {
            // Remove individual pipeline cache if ID is known
            if (pipelineId.HasValue)
            {
                await _cache.RemoveAsync(CacheKeys.Pipeline(pipelineId.Value));
            }

            // Remove paginated list caches (pages 1-10 should cover most cases)
            for (int page = 1; page <= 10; page++)
            {
                await _cache.RemoveAsync(CacheKeys.PipelineList(page, 20));
                await _cache.RemoveAsync(CacheKeys.PipelineList(page, 50));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate pipeline cache");
        }
    }

    public async Task UpdateBuildJobAsync(BuildJob job)
    {
        _db.BuildJobs.Update(job);
        await _db.SaveChangesAsync();
        try
        {
            await _cache.RemoveAsync(CacheKeys.BuildJob(job.Id));
            await _cache.RemoveAsync(CacheKeys.BuildQueue);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate cache for build job {JobId}", job.Id);
        }
    }
}
