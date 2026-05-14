using Lumina.Shared.DTOs;
using Lumina.Shared.Models.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Lumina.BuildService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BuildsController : ControllerBase
{
    private readonly Services.PipelineEngine _engine;
    private readonly Services.DockerBuildService _dockerBuild;
    private readonly ILogger<BuildsController> _logger;

    public BuildsController(Services.PipelineEngine engine, Services.DockerBuildService dockerBuild, ILogger<BuildsController> logger)
    {
        _engine = engine;
        _dockerBuild = dockerBuild;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<BuildListResponse>>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var (builds, totalCount) = await _engine.ListBuildJobsAsync(page, pageSize);
        var response = new BuildListResponse(
            builds.Select(b => new BuildJobSummaryResponse(b.Id, b.PipelineId, b.Status, b.SpecName, b.CreatedAt, b.TriggeredBy, b.CommitSha, b.Branch)).ToList(),
            totalCount, page, pageSize);
        return Ok(new ApiResponse<BuildListResponse>(true, response, null, null));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<BuildJobResponse>>> Get(Guid id)
    {
        var job = await _engine.GetBuildJobAsync(id);
        if (job == null) return NotFound(new ApiResponse<BuildJobResponse>(false, null, "Not found", null));
        var response = new BuildJobResponse(job.Id, job.PipelineId, job.Status, job.SpecName, job.ContainerId,
            job.Logs, job.CreatedAt, job.StartedAt, job.CompletedAt, job.TriggeredBy,
            job.Artifacts.Select(a => new BuildArtifactResponse(a.Id, a.FileName, a.FileSize, a.HashSha256, a.HashMd5, a.PgpSignature, a.CveScanStatus)).ToList(),
            job.SourceUrl, job.CommitSha, job.Branch, job.CommitMessage, job.CommitAuthor);
        return Ok(new ApiResponse<BuildJobResponse>(true, response, null, null));
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<object>>> Cancel(Guid id)
    {
        var result = await _dockerBuild.CancelBuildAsync(id);
        if (!result) return BadRequest(new ApiResponse<object>(false, null, "Failed to cancel build", null));
        return Ok(new ApiResponse<object>(true, null, null, "Build cancelled"));
    }

    [HttpGet("queue")]
    public async Task<ActionResult<ApiResponse<BuildQueueResponse>>> Queue()
    {
        var active = await _engine.GetActiveBuildsAsync();
        var queued = active.Where(b => b.Status == BuildStatus.Queued)
            .Select(b => new BuildJobSummaryResponse(b.Id, b.PipelineId, b.Status, b.SpecName, b.CreatedAt, b.TriggeredBy, b.CommitSha, b.Branch)).ToList();
        var running = active.Where(b => b.Status == BuildStatus.Building)
            .Select(b => new BuildJobSummaryResponse(b.Id, b.PipelineId, b.Status, b.SpecName, b.CreatedAt, b.TriggeredBy, b.CommitSha, b.Branch)).ToList();
        var response = new BuildQueueResponse(queued, running, queued.Count, running.Count);
        return Ok(new ApiResponse<BuildQueueResponse>(true, response, null, null));
    }

    [HttpGet("{id:guid}/logs")]
    public async Task<ActionResult<ApiResponse<string>>> Logs(Guid id)
    {
        var job = await _engine.GetBuildJobAsync(id);
        if (job == null) return NotFound(new ApiResponse<string>(false, null, "Not found", null));
        return Ok(new ApiResponse<string>(true, job.Logs, null, null));
    }

    [HttpGet("{id:guid}/spec")]
    public async Task<IActionResult> DownloadSpec(Guid id)
    {
        var job = await _engine.GetBuildJobAsync(id);
        if (job == null) return NotFound();
        if (string.IsNullOrEmpty(job.SpecContent)) return NotFound("No spec content saved for this build");

        var fileName = job.SpecName.EndsWith(".spec") ? job.SpecName : $"{job.SpecName}.spec";
        return File(System.Text.Encoding.UTF8.GetBytes(job.SpecContent), "text/plain", fileName);
    }
}
