using Lumina.Shared.DTOs;
using Lumina.Shared.Models.Enums;
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
    public async Task<ActionResult<ApiResponse<PipelineListResponse>>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        try
        {
            var (pipelines, totalCount) = await _engine.ListPipelinesAsync(page, pageSize);
            var response = new PipelineListResponse(
                pipelines.Select(p => new PipelineSummaryResponse(p.Id, p.Name, p.Description, p.Status, p.CreatedBy, p.CreatedAt, p.Steps.Count, p.GitRepoUrl, p.GitBranch)).ToList(),
                totalCount, page, pageSize);
            return Ok(new ApiResponse<PipelineListResponse>(true, response, null, null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list pipelines (page={Page}, pageSize={PageSize})", page, pageSize);
            return StatusCode(500, new ApiResponse<PipelineListResponse>(false, null, $"Failed to load pipelines: {ex.Message}", null));
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<PipelineResponse>>> Get(Guid id)
    {
        var p = await _engine.GetPipelineAsync(id);
        if (p == null) return NotFound(new ApiResponse<PipelineResponse>(false, null, "Not found", null));
        var webhookUrl = $"{Request.Scheme}://{Request.Host}/api/webhooks/{p.Id}";
        var response = new PipelineResponse(p.Id, p.Name, p.Description, p.Status,
            p.Steps.Select(s => new PipelineStepResponse(s.Id, s.Type, s.Name, s.Order, s.Status, s.Configuration)).ToList(),
            p.CreatedBy, p.CreatedAt, p.UpdatedAt, p.Tags, p.GitRepoUrl, p.GitBranch, p.SpecPath, webhookUrl, p.BuildImage,
            p.GitUsername, !string.IsNullOrEmpty(p.GitToken), p.SpecContent);
        return Ok(new ApiResponse<PipelineResponse>(true, response, null, null));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<PipelineResponse>>> Create([FromBody] CreatePipelineRequest request)
    {
        try
        {
            var p = await _engine.CreatePipelineAsync(request, "system");
            var webhookUrl = $"{Request.Scheme}://{Request.Host}/api/webhooks/{p.Id}";
            var response = new PipelineResponse(p.Id, p.Name, p.Description, p.Status,
                p.Steps.Select(s => new PipelineStepResponse(s.Id, s.Type, s.Name, s.Order, s.Status, s.Configuration)).ToList(),
                p.CreatedBy, p.CreatedAt, p.UpdatedAt, p.Tags, p.GitRepoUrl, p.GitBranch, p.SpecPath, webhookUrl, p.BuildImage,
                p.GitUsername, !string.IsNullOrEmpty(p.GitToken), p.SpecContent);
            return CreatedAtAction(nameof(Get), new { id = p.Id }, new ApiResponse<PipelineResponse>(true, response, null, "Pipeline created"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create pipeline {Name}", request.Name);
            return StatusCode(500, new ApiResponse<PipelineResponse>(false, null, $"Failed to create pipeline: {ex.Message}", null));
        }
    }

    [HttpPost("{id:guid}/trigger")]
    public async Task<ActionResult<ApiResponse<BuildJobResponse>>> Trigger(Guid id, [FromBody] TriggerBuildRequest request)
    {
        try
        {
            var job = await _engine.TriggerBuildAsync(id, request);
            var response = new BuildJobResponse(job.Id, job.PipelineId, job.Status, job.SpecName, job.ContainerId, job.Logs, job.CreatedAt, job.StartedAt, job.CompletedAt, job.TriggeredBy, [], job.SourceUrl, job.CommitSha, job.Branch, job.CommitMessage, job.CommitAuthor);
            return Ok(new ApiResponse<BuildJobResponse>(true, response, null, "Build triggered"));
        }
        catch (InvalidOperationException ex)
        {
            // Includes the Sign-step key gate (no active PGP key) and
            // "pipeline not found"; surface as 400 rather than a raw 500.
            return BadRequest(new ApiResponse<BuildJobResponse>(false, null, ex.Message, null));
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResponse<PipelineResponse>>> Update(Guid id, [FromBody] UpdatePipelineRequest request)
    {
        try
        {
            var p = await _engine.UpdatePipelineAsync(id, request);
            var webhookUrl = $"{Request.Scheme}://{Request.Host}/api/webhooks/{p.Id}";
            var response = new PipelineResponse(p.Id, p.Name, p.Description, p.Status,
                p.Steps.Select(s => new PipelineStepResponse(s.Id, s.Type, s.Name, s.Order, s.Status, s.Configuration)).ToList(),
                p.CreatedBy, p.CreatedAt, p.UpdatedAt, p.Tags, p.GitRepoUrl, p.GitBranch, p.SpecPath, webhookUrl, p.BuildImage,
                p.GitUsername, !string.IsNullOrEmpty(p.GitToken), p.SpecContent);
            return Ok(new ApiResponse<PipelineResponse>(true, response, null, "Pipeline updated"));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ApiResponse<PipelineResponse>(false, null, ex.Message, null));
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id)
    {
        try
        {
            var deleted = await _engine.DeletePipelineAsync(id);
            if (!deleted) return NotFound(new ApiResponse<object>(false, null, "Pipeline not found", null));
            return Ok(new ApiResponse<object>(true, null, null, "Pipeline deleted"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<object>(false, null, ex.Message, null));
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
            var response = new BuildJobResponse(job.Id, job.PipelineId, job.Status, job.SpecName, job.ContainerId, job.Logs, job.CreatedAt, job.StartedAt, job.CompletedAt, job.TriggeredBy, [], job.SourceUrl, job.CommitSha, job.Branch, job.CommitMessage, job.CommitAuthor);
            return Ok(new ApiResponse<BuildJobResponse>(true, response, null, "Auto build triggered — sources will be fetched from git"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<BuildJobResponse>(false, null, ex.Message, null));
        }
    }
}