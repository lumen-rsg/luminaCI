using Lumina.BuildService.Data;
using Lumina.Shared.Errors;
using Lumina.Shared.Events;
using Lumina.Shared.Extensions;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

public class PipelineEngine
{
    private readonly BuildDbContext _db;
    private readonly DockerBuildService _dockerBuild;
    private readonly ILogger<PipelineEngine> _logger;
    private readonly RedisCacheService _cache;
    private readonly IBus _bus;

    public PipelineEngine(BuildDbContext db, DockerBuildService dockerBuild, ILogger<PipelineEngine> logger, RedisCacheService cache, IBus bus)
    {
        _db = db;
        _dockerBuild = dockerBuild;
        _logger = logger;
        _cache = cache;
        _bus = bus;
    }

    /// <summary>
    /// Verifies an active PGP signing key exists before allowing a build of a
    /// pipeline that declares a <see cref="StepType.Sign"/> step. Without an
    /// active key the build would silently degrade to an unsigned artifact that
    /// then cannot be published — failing the trigger early gives the operator a
    /// clear, recoverable error instead of a wasted build.
    /// </summary>
    private async Task RequireActiveSigningKeyAsync()
    {
        try
        {
            var response = await _bus.Request<GetActiveSigningKey, ActiveSigningKey>(
                new GetActiveSigningKey(), timeout: TimeSpan.FromSeconds(10));

            if (!response.Message.KeyId.HasValue)
            {
                throw new ValidationException(
                    "No active PGP key. Generate a key in Security settings before triggering a pipeline that includes a Sign step — unsigned artifacts cannot be published.");
            }
        }
        catch (ValidationException)
        {
            throw; // our own gate message — propagate verbatim
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to confirm an active PGP key exists via the SecurityService bus; rejecting build trigger (fail-closed)");
            throw new ValidationException(
                "Could not confirm an active PGP signing key (SecurityService unreachable). Cannot start a Sign-enabled build without assurance that the artifact can be signed.");
        }
    }

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
            SpecContent = request.SpecContent,
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
                // Pipeline-level extra sources directory
                var pipelineExtraDir = $"/opt/lumina/extra-sources/pipelines/{pipelineId}";
                var pipelineExtraExists = Directory.Exists(pipelineExtraDir) && Directory.GetFiles(pipelineExtraDir, "*", SearchOption.AllDirectories).Length > 0;

                await _dockerBuild.StartBuildAsync(job, specContent, sourceUrl, pipeline.BuildImage,
                    pipeline.GitUsername, pipeline.GitToken,
                    extraSourcesPipelineDir: pipelineExtraExists ? pipelineExtraDir : null);
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
            throw new NotFoundException($"Pipeline {pipelineId} not found");

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

    /// <summary>
    /// Trigger a build using pre-fetched sources from the SourceService.
    /// This is used when sources are downloaded by the SourceService (git, http, ftp, rsync, svn, hg).
    /// The SourceService downloads sources to MinIO, then we download them locally and mount into the build container.
    /// </summary>
    /// <remarks>
    /// This path is NOT subject to the Sign-step key gate because the pipelines
    /// it auto-creates (see below) declare only a Build step — there is no Sign
    /// step to satisfy. Unsigned artifacts produced here are still caught
    /// downstream: the CveScanCompletedConsumer fails the build when no active
    /// key exists, and the RepositoryService publish gate rejects any artifact
    /// lacking a stored PGP signature.
    /// </remarks>
    public async Task<BuildJob> TriggerBuildFromConfigAsync(
        string packageName, string sourceDir, string specContent, string specName,
        string? buildImage = null, string triggeredBy = "source-service")
    {
        // Find or create a pipeline for this package
        var pipeline = await _db.Pipelines
            .Include(p => p.Steps)
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

        // Start the build container (with or without a formal build step)
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

        return job;
    }

    public async Task<(List<Pipeline> Items, int TotalCount)> ListPipelinesAsync(int page = 1, int pageSize = 20)
    {
        // Do NOT cache list queries — EF Core entities with navigation properties
        // (ValueTuple + System.Text.Json) cause broken deserialization that makes
        // pipelines disappear on refresh.
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
        // Direct DB query — avoid caching EF Core entities with navigation properties
        // (System.Text.Json cannot round-trip them correctly through Redis)
        return await _db.Pipelines
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<BuildJob?> GetBuildJobAsync(Guid id)
    {
        // Direct DB query — avoid caching EF Core entities with navigation properties
        return await _db.BuildJobs
            .Include(b => b.Artifacts)
            .FirstOrDefaultAsync(b => b.Id == id);
    }

    public async Task<(List<BuildJob> Items, int TotalCount)> ListBuildJobsAsync(int page = 1, int pageSize = 20)
    {
        // Do NOT cache list queries — same issue as ListPipelinesAsync:
        // ValueTuple + System.Text.Json + EF Core entities = broken deserialization
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

        pipeline.Name = request.Name;
        pipeline.Description = request.Description;
        pipeline.Tags = request.Tags;
        pipeline.UpdatedAt = DateTime.UtcNow;

        // Update git fields if provided
        if (request.GitRepoUrl != null) pipeline.GitRepoUrl = request.GitRepoUrl;
        if (request.GitBranch != null) pipeline.GitBranch = request.GitBranch;
        if (request.SpecPath != null) pipeline.SpecPath = request.SpecPath;
        if (request.BuildImage != null) pipeline.BuildImage = request.BuildImage;
        if (request.GitUsername != null) pipeline.GitUsername = request.GitUsername;
        if (request.GitToken != null) pipeline.GitToken = request.GitToken;
        if (request.SpecContent != null) pipeline.SpecContent = request.SpecContent;

        // Replace steps
        _db.PipelineSteps.RemoveRange(pipeline.Steps);
        pipeline.Steps = request.Steps.Select((s, i) => new PipelineStep
        {
            Id = Guid.NewGuid(),
            PipelineId = pipeline.Id,
            Type = s.Type,
            Name = s.Name,
            Order = s.Order,
            Status = StepStatus.Pending,
            Configuration = s.Configuration
        }).ToList();

        _db.Pipelines.Update(pipeline);
        await _db.SaveChangesAsync();

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
            .ToListAsync();

        foreach (var job in queuedJobs)
        {
            job.Status = BuildStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
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
