using Lumina.Shared.DTOs;
using Lumina.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace Lumina.ScannerService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ScannerController : ControllerBase
{
    private readonly Services.TrivyScannerService _scanner;
    private readonly ILogger<ScannerController> _logger;

    public ScannerController(Services.TrivyScannerService scanner, ILogger<ScannerController> logger)
    {
        _scanner = scanner;
        _logger = logger;
    }

    [HttpPost("scan")]
    public async Task<ActionResult<ApiResponse<CveReport>>> ScanArtifact([FromBody] ScanRequest request)
    {
        // SECURITY: Validate artifact ID format
        if (request.ArtifactId == Guid.Empty)
            return BadRequest(new ApiResponse<CveReport>(false, null, "Invalid artifact ID", null));

        var report = await _scanner.ScanArtifactAsync(request.ArtifactId, request.ArtifactPath ?? "", request.ScannerType);
        return Accepted(new ApiResponse<CveReport>(true, report, null, "Scan started"));
    }

    [HttpGet("reports/{id:guid}")]
    public async Task<ActionResult<ApiResponse<CveReport>>> GetReport(Guid id)
    {
        var report = await _scanner.GetReportAsync(id);
        if (report == null) return NotFound(new ApiResponse<CveReport>(false, null, "Report not found", null));
        return Ok(new ApiResponse<CveReport>(true, report, null, null));
    }

    [HttpGet("reports/artifact/{artifactId:guid}")]
    public async Task<ActionResult<ApiResponse<List<CveReport>>>> GetArtifactReports(Guid artifactId)
    {
        var reports = await _scanner.GetArtifactReportsAsync(artifactId);
        return Ok(new ApiResponse<List<CveReport>>(true, reports, null, null));
    }

    [HttpGet("reports/recent")]
    public async Task<ActionResult<ApiResponse<List<CveReport>>>> GetRecentReports([FromQuery] int count = 20)
    {
        // Clamp to prevent excessive data retrieval
        count = Math.Clamp(count, 1, 100);
        var reports = await _scanner.GetRecentReportsAsync(count);
        return Ok(new ApiResponse<List<CveReport>>(true, reports, null, null));
    }

    [HttpGet("scans")]
    public async Task<ActionResult<ApiResponse<ScanListResponse>>> ListScans([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        page = Math.Clamp(page, 1, 1000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await _scanner.GetScansPaginatedAsync(page, pageSize);
        return Ok(new ApiResponse<ScanListResponse>(true, result, null, null));
    }
}
