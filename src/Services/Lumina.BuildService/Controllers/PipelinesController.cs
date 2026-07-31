using Lumina.Shared.DTOs;
using Lumina.Shared.Models.Enums;
using Lumina.Web.Shared.Authorization;
using Lumina.Web.Shared.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lumina.BuildService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // Defense-in-depth (see SecurityController): re-validate the JWT here
            // too, so a directly-reached internal port is not anonymous.
public class PipelinesController : ControllerBase
{
    private readonly Services.PipelineEngine _engine;
    private readonly ILogger<PipelinesController> _logger;

    public PipelinesController(Services.PipelineEngine engine, ILogger<PipelinesController> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PipelineListResponse>>> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null)
    {
        try
        {
            var (pipelines, totalCount) = await _engine.ListPipelinesAsync(page, pageSize, search);
            var response = new PipelineListResponse(
                pipelines.Select(p => new PipelineSummaryResponse(p.Id, p.Name, p.Description, p.Status, p.CreatedBy, p.CreatedAt, p.Steps.Count, p.GitRepoUrl, p.GitBranch)).ToList(),
                totalCount, page, pageSize);
            return Ok(new ApiResponse<PipelineListResponse>(true, response, null, null));
        }
        catch (Exception ex)
        {
            // ex.Message may contain DB/stack hints — never return it. The full
            // exception is logged here; the client gets a fixed message (SEC-022).
            return ApiResults.FromException<PipelineListResponse>(ex, _logger, "Pipelines.List", page, pageSize);
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<PipelineResponse>>> Get(Guid id)
    {
        var p = await _engine.GetPipelineAsync(id);
        if (p == null) return NotFound(new ApiResponse<PipelineResponse>(false, null, "Not found", null));
        var webhookUrl = $"{Request.Scheme}://{Request.Host}/api/webhooks/{p.Id}";
        var response = new PipelineResponse(p.Id, p.Name, p.Description, p.Status,
            p.Steps.Select(s => new PipelineStepResponse(s.Id, s.Type, s.Name, s.Order, s.Configuration)).ToList(),
            p.CreatedBy, p.CreatedAt, p.UpdatedAt, p.Tags, p.GitRepoUrl, p.GitBranch, p.SpecPath, webhookUrl, p.BuildImage,
            p.GitUsername, !string.IsNullOrEmpty(p.GitToken), p.SpecContent,
            p.TargetDistribution, p.TargetRelease, p.TargetArchitecture, p.BuildProfile,
            p.TriggerPaths);
        return Ok(new ApiResponse<PipelineResponse>(true, response, null, null));
    }

    [HttpPost]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<ApiResponse<PipelineResponse>>> Create([FromBody] CreatePipelineRequest request)
    {
        try
        {
            var p = await _engine.CreatePipelineAsync(request, "system");
            var webhookUrl = $"{Request.Scheme}://{Request.Host}/api/webhooks/{p.Id}";
            var response = new PipelineResponse(p.Id, p.Name, p.Description, p.Status,
                p.Steps.Select(s => new PipelineStepResponse(s.Id, s.Type, s.Name, s.Order, s.Configuration)).ToList(),
                p.CreatedBy, p.CreatedAt, p.UpdatedAt, p.Tags, p.GitRepoUrl, p.GitBranch, p.SpecPath, webhookUrl, p.BuildImage,
                p.GitUsername, !string.IsNullOrEmpty(p.GitToken), p.SpecContent,
                p.TargetDistribution, p.TargetRelease, p.TargetArchitecture, p.BuildProfile,
                p.TriggerPaths);
            return CreatedAtAction(nameof(Get), new { id = p.Id }, new ApiResponse<PipelineResponse>(true, response, null, "Pipeline created"));
        }
        catch (Exception ex)
        {
            // Validation gates (e.g. missing WebhookSecret, SEC-020) now throw
            // ValidationException and surface as a recoverable 400 with their
            // own message; any other fault is logged and returned as a generic
            // 500 (never ex.Message — see SEC-022).
            return ApiResults.FromException<PipelineResponse>(ex, _logger, "Pipelines.Create", request.Name);
        }
    }

    [HttpPost("{id:guid}/trigger")]
    public async Task<ActionResult<ApiResponse<BuildJobResponse>>> Trigger(Guid id, [FromBody] TriggerBuildRequest request)
    {
        try
        {
            var job = await _engine.TriggerBuildAsync(id, request);
            var response = new BuildJobResponse(
                job.Id, job.PipelineId, job.Status, job.SpecName, job.ContainerId, job.Logs,
                job.CreatedAt, job.StartedAt, job.CompletedAt, job.TriggeredBy, [],
                job.SourceUrl, job.CommitSha, job.Branch, job.CommitMessage, job.CommitAuthor,
                job.StepRuns.Select(ToStepRunResponse).ToList(),
                job.TargetDistribution, job.TargetRelease, job.TargetArchitecture, job.BuildProfile,
                job.RunnerImageReference, job.RunnerImageDigest);
            return Ok(new ApiResponse<BuildJobResponse>(true, response, null, "Build triggered"));
        }
        catch (Exception ex)
        {
            // NotFoundException (missing pipeline) -> 404; ValidationException
            // (Sign-step key gate) -> 400; anything else -> generic 500 (SEC-022).
            return ApiResults.FromException<BuildJobResponse>(ex, _logger, "Pipelines.Trigger", id);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<ApiResponse<PipelineResponse>>> Update(Guid id, [FromBody] UpdatePipelineRequest request)
    {
        try
        {
            var p = await _engine.UpdatePipelineAsync(id, request);
            var webhookUrl = $"{Request.Scheme}://{Request.Host}/api/webhooks/{p.Id}";
            var response = new PipelineResponse(p.Id, p.Name, p.Description, p.Status,
                p.Steps.Select(s => new PipelineStepResponse(s.Id, s.Type, s.Name, s.Order, s.Configuration)).ToList(),
                p.CreatedBy, p.CreatedAt, p.UpdatedAt, p.Tags, p.GitRepoUrl, p.GitBranch, p.SpecPath, webhookUrl, p.BuildImage,
                p.GitUsername, !string.IsNullOrEmpty(p.GitToken), p.SpecContent,
                p.TargetDistribution, p.TargetRelease, p.TargetArchitecture, p.BuildProfile,
                p.TriggerPaths);
            return Ok(new ApiResponse<PipelineResponse>(true, response, null, "Pipeline updated"));
        }
        catch (Exception ex)
        {
            // NotFoundException (missing pipeline) -> 404 with its own message;
            // other faults -> generic 500 (SEC-022).
            return ApiResults.FromException<PipelineResponse>(ex, _logger, "Pipelines.Update", id);
        }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id)
    {
        try
        {
            var deleted = await _engine.DeletePipelineAsync(id);
            if (!deleted) return NotFound(new ApiResponse<object>(false, null, "Pipeline not found", null));
            return Ok(new ApiResponse<object>(true, null, null, "Pipeline deleted"));
        }
        catch (Exception ex)
        {
            // ConflictException (active builds) -> 409; other faults -> 500 (SEC-022).
            return ApiResults.FromException<object>(ex, _logger, "Pipelines.Delete", id);
        }
    }

    /// <summary>
    /// Trigger an automatic build using the pipeline's configured git repository.
    /// No request body needed — sources are fetched from git automatically.
    /// </summary>
    [HttpPost("{id:guid}/trigger-auto")]
    public async Task<ActionResult<ApiResponse<BuildJobResponse>>> TriggerAuto(Guid id, [FromBody] TriggerAutoBuildRequest? request = null)
    {
        try
        {
            var triggeredBy = request?.TriggeredBy ?? "auto";
            var job = await _engine.TriggerAutoBuildAsync(id, triggeredBy);
            var response = new BuildJobResponse(
                job.Id, job.PipelineId, job.Status, job.SpecName, job.ContainerId, job.Logs,
                job.CreatedAt, job.StartedAt, job.CompletedAt, job.TriggeredBy, [],
                job.SourceUrl, job.CommitSha, job.Branch, job.CommitMessage, job.CommitAuthor,
                job.StepRuns.Select(ToStepRunResponse).ToList(),
                job.TargetDistribution, job.TargetRelease, job.TargetArchitecture, job.BuildProfile,
                job.RunnerImageReference, job.RunnerImageDigest);
            return Ok(new ApiResponse<BuildJobResponse>(true, response, null, "Auto build triggered — sources will be fetched from git"));
        }
        catch (Exception ex)
        {
            // NotFoundException (missing pipeline) -> 404; ValidationException
            // (no GitRepoUrl) -> 400; other faults -> generic 500 (SEC-022).
            return ApiResults.FromException<BuildJobResponse>(ex, _logger, "Pipelines.TriggerAuto", id);
        }
    }

    private static BuildStepRunResponse ToStepRunResponse(Lumina.Shared.Models.BuildStepRun step) =>
        new(step.Id, step.Type, step.Name, step.Order, step.Status, step.StartedAt, step.CompletedAt, step.Error);
}
