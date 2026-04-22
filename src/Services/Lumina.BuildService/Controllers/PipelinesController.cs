using Lumina.Shared.DTOs;
using Lumina.Shared.Models.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Lumina.BuildService.Controllers;

[ApiController]
[Route("api/[controller]")]
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
        var pipelines = await _engine.ListPipelinesAsync(page, pageSize);
        var response = new PipelineListResponse(
            pipelines.Select(p => new PipelineSummaryResponse(p.Id, p.Name, p.Description, p.Status, p.CreatedBy, p.CreatedAt, p.Steps.Count)).ToList(),
            pipelines.Count, page, pageSize);
        return Ok(new ApiResponse<PipelineListResponse>(true, response, null, null));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<PipelineResponse>>> Get(Guid id)
    {
        var p = await _engine.GetPipelineAsync(id);
        if (p == null) return NotFound(new ApiResponse<PipelineResponse>(false, null, "Not found", null));
        var webhookUrl = $"{Request.Scheme}://{Request.Host}/api/webhooks/{p.Id}";
        var response = new PipelineResponse(p.Id, p.Name, p.Description, p.Status,
            p.Steps.Select(s => new PipelineStepResponse(s.Id, s.Type, s.Name, s.Order, s.Status, s.Configuration)).ToList(),
            p.CreatedBy, p.CreatedAt, p.UpdatedAt, p.Tags, p.GitRepoUrl, p.GitBranch, p.SpecPath, webhookUrl);
        return Ok(new ApiResponse<PipelineResponse>(true, response, null, null));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<PipelineResponse>>> Create([FromBody] CreatePipelineRequest request)
    {
        var p = await _engine.CreatePipelineAsync(request, "system");
        var webhookUrl = $"{Request.Scheme}://{Request.Host}/api/webhooks/{p.Id}";
        var response = new PipelineResponse(p.Id, p.Name, p.Description, p.Status,
            p.Steps.Select(s => new PipelineStepResponse(s.Id, s.Type, s.Name, s.Order, s.Status, s.Configuration)).ToList(),
            p.CreatedBy, p.CreatedAt, p.UpdatedAt, p.Tags, p.GitRepoUrl, p.GitBranch, p.SpecPath, webhookUrl);
        return CreatedAtAction(nameof(Get), new { id = p.Id }, new ApiResponse<PipelineResponse>(true, response, null, "Pipeline created"));
    }

    [HttpPost("{id:guid}/trigger")]
    public async Task<ActionResult<ApiResponse<BuildJobResponse>>> Trigger(Guid id, [FromBody] TriggerBuildRequest request)
    {
        var job = await _engine.TriggerBuildAsync(id, request);
        var response = new BuildJobResponse(job.Id, job.PipelineId, job.Status, job.SpecName, job.ContainerId, job.Logs, job.CreatedAt, job.StartedAt, job.CompletedAt, job.TriggeredBy, []);
        return Ok(new ApiResponse<BuildJobResponse>(true, response, null, "Build triggered"));
    }
}