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
        try
        {
            var (builds, totalCount) = await _engine.ListBuildJobsAsync(page, pageSize);
            var response = new BuildListResponse(
                builds.Select(b => new BuildJobSummaryResponse(b.Id, b.PipelineId, b.Status, b.SpecName, b.CreatedAt, b.TriggeredBy, b.CommitSha, b.Branch)).ToList(),
                totalCount, page, pageSize);
            return Ok(new ApiResponse<BuildListResponse>(true, response, null, null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list builds (page={Page}, pageSize={PageSize})", page, pageSize);
            return StatusCode(500, new ApiResponse<BuildListResponse>(false, null, $"Failed to load builds: {ex.Message}", null));
        }
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

    [HttpDelete("queue/clear")]
    public async Task<ActionResult<ApiResponse<object>>> ClearQueue()
    {
        var count = await _engine.ClearQueuedBuildsAsync();
        return Ok(new ApiResponse<object>(true, null, null, $"Cleared {count} queued build(s)"));
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

    /// <summary>
    /// Download a built RPM artifact by file name
    /// </summary>
    [HttpGet("{id:guid}/artifacts/{fileName}")]
    public async Task<IActionResult> DownloadArtifact(Guid id, string fileName)
    {
        var job = await _engine.GetBuildJobAsync(id);
        if (job == null) return NotFound(new { error = "Build not found" });

        var artifact = job.Artifacts.FirstOrDefault(a => a.FileName == fileName);
        if (artifact == null) return NotFound(new { error = $"Artifact '{fileName}' not found for this build" });

        if (string.IsNullOrEmpty(artifact.FilePath) || !System.IO.File.Exists(artifact.FilePath))
            return NotFound(new { error = "Artifact file not found on disk" });

        var contentType = fileName.EndsWith(".src.rpm") ? "application/x-rpm" : "application/x-rpm";
        return File(System.IO.File.OpenRead(artifact.FilePath), contentType, fileName);
    }

    /// <summary>
    /// Trigger a build from pre-fetched sources (called by SourceService after conf.ini fetch).
    /// Receives packageName, sourceDir, specContent, specName.
    /// </summary>
    [HttpPost("trigger-from-config")]
    public async Task<ActionResult<ApiResponse<BuildJobResponse>>> TriggerFromConfig([FromBody] TriggerFromConfigRequest request)
    {
        try
        {
            var job = await _engine.TriggerBuildFromConfigAsync(
                request.PackageName,
                request.SourceDir,
                request.SpecContent,
                request.SpecName,
                request.BuildImage,
                "source-service");

            var response = new BuildJobResponse(job.Id, job.PipelineId, job.Status, job.SpecName, job.ContainerId,
                job.Logs, job.CreatedAt, job.StartedAt, job.CompletedAt, job.TriggeredBy,
                [], job.SourceUrl, job.CommitSha, job.Branch, job.CommitMessage, job.CommitAuthor);

            return Ok(new ApiResponse<BuildJobResponse>(true, response, null, "Build triggered from conf.ini"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger build from config for {Package}", request.PackageName);
            return StatusCode(500, new ApiResponse<BuildJobResponse>(false, null, ex.Message, null));
        }
    }

    /// <summary>
    /// Update CVE scan status for a specific artifact (called by ScannerService after scan completion).
    /// </summary>
    [HttpPut("artifacts/{artifactId:guid}/scan-status")]
    public async Task<ActionResult<ApiResponse<object>>> UpdateArtifactScanStatus(Guid artifactId, [FromQuery] string status)
    {
        var job = await _engine.GetBuildJobByArtifactIdAsync(artifactId);
        if (job == null) return NotFound(new ApiResponse<object>(false, null, "Artifact not found", null));

        var artifact = job.Artifacts.FirstOrDefault(a => a.Id == artifactId);
        if (artifact == null) return NotFound(new ApiResponse<object>(false, null, "Artifact not found in build", null));

        artifact.CveScanStatus = status?.ToLower() switch
        {
            "completed" => Lumina.Shared.Models.Enums.ScanStatus.Completed,
            "failed" => Lumina.Shared.Models.Enums.ScanStatus.Failed,
            "running" => Lumina.Shared.Models.Enums.ScanStatus.Running,
            _ => Lumina.Shared.Models.Enums.ScanStatus.Failed
        };

        await _engine.UpdateBuildJobAsync(job);
        _logger.LogInformation("Updated CVE scan status for artifact {ArtifactId} to {Status}", artifactId, status);

        return Ok(new ApiResponse<object>(true, null, null, $"Scan status updated to {status}"));
    }

    /// <summary>
    /// Update PGP signature for a specific artifact (called by SecurityService after signing).
    /// </summary>
    [HttpPut("artifacts/{artifactId:guid}/pgp-signature")]
    public async Task<ActionResult<ApiResponse<object>>> UpdateArtifactPgpSignature(Guid artifactId, [FromBody] UpdatePgpSignatureRequest request)
    {
        var job = await _engine.GetBuildJobByArtifactIdAsync(artifactId);
        if (job == null) return NotFound(new ApiResponse<object>(false, null, "Artifact not found", null));

        var artifact = job.Artifacts.FirstOrDefault(a => a.Id == artifactId);
        if (artifact == null) return NotFound(new ApiResponse<object>(false, null, "Artifact not found in build", null));

        artifact.PgpSignature = request.PgpSignature;

        await _engine.UpdateBuildJobAsync(job);
        _logger.LogInformation("Updated PGP signature for artifact {ArtifactId}", artifactId);

        return Ok(new ApiResponse<object>(true, null, null, "PGP signature updated"));
    }

    /// <summary>
    /// Download all artifacts — for a single artifact serves it directly,
    /// for multiple artifacts creates a zip archive.
    /// </summary>
    [HttpGet("{id:guid}/artifacts")]
    public async Task<IActionResult> DownloadAllArtifacts(Guid id)
    {
        var job = await _engine.GetBuildJobAsync(id);
        if (job == null) return NotFound(new { error = "Build not found" });

        if (!job.Artifacts.Any())
            return NotFound(new { error = "No artifacts available for this build" });

        // If only one artifact, serve it directly
        if (job.Artifacts.Count == 1)
        {
            var single = job.Artifacts.First();
            if (!string.IsNullOrEmpty(single.FilePath) && System.IO.File.Exists(single.FilePath))
                return File(System.IO.File.OpenRead(single.FilePath), "application/x-rpm", single.FileName);
            return NotFound(new { error = "Artifact file not found on disk" });
        }

        // Multiple artifacts — create a zip archive on the fly
        var archiveName = $"build-{id.ToString("N")[..8]}-artifacts.zip";
        var tempArchive = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"artifacts-{id:N}.zip");

        try
        {
            using (var zipStream = new System.IO.FileStream(tempArchive, System.IO.FileMode.Create))
            using (var zip = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var artifact in job.Artifacts)
                {
                    if (string.IsNullOrEmpty(artifact.FilePath) || !System.IO.File.Exists(artifact.FilePath))
                        continue;
                    var entry = zip.CreateEntry(artifact.FileName, System.IO.Compression.CompressionLevel.Fastest);
                    using var entryStream = entry.Open();
                    using var fileStream = System.IO.File.OpenRead(artifact.FilePath);
                    await fileStream.CopyToAsync(entryStream);
                }
            }
        }
        catch
        {
            // Fallback: serve the first available artifact
            var firstArtifact = job.Artifacts.FirstOrDefault(a =>
                !string.IsNullOrEmpty(a.FilePath) && System.IO.File.Exists(a.FilePath));
            if (firstArtifact != null)
                return File(System.IO.File.OpenRead(firstArtifact.FilePath), "application/x-rpm", firstArtifact.FileName);
            return NotFound(new { error = "Could not create archive" });
        }

        return File(System.IO.File.OpenRead(tempArchive), "application/zip", archiveName);
    }
}
