using Lumina.Shared.DTOs;
using Lumina.Shared.Models;
using Lumina.Web.Shared.Authorization;
using Lumina.Web.Shared.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lumina.ScannerService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // Defense-in-depth (see SecurityController): re-validate the JWT here
            // too, so a directly-reached internal port is not anonymous.
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

        try
        {
            var report = await _scanner.ScanArtifactAsync(request.ArtifactId, request.ArtifactPath ?? "", request.ScannerType);
            return Accepted(new ApiResponse<CveReport>(true, report, null, "Scan started"));
        }
        catch (ArgumentException ex)
        {
            // Path confinement failure (traversal / control chars in
            // ArtifactPath). The message is application-authored and safe, but
            // route it through the domain mapper so it lands as a 400 rather
            // than leaking out as a raw 500.
            _logger.LogWarning(ex, "Rejected scan request for artifact {ArtifactId} (invalid path)", request.ArtifactId);
            return BadRequest(new ApiResponse<CveReport>(false, null, "The supplied artifact path is invalid.", null));
        }
        catch (Exception ex)
        {
            // DB / Trivy faults: log server-side, return a fixed message (SEC-022).
            return ApiResults.FromException<CveReport>(ex, _logger, "Scanner.ScanArtifact", request.ArtifactId);
        }
    }

    [HttpGet("scans")]
    public async Task<ActionResult<ApiResponse<ScanPaginatedResponse>>> ListScans([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        page = Math.Clamp(page, 1, 1000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await _scanner.GetScansPaginatedAsync(page, pageSize);
        return Ok(new ApiResponse<ScanPaginatedResponse>(true, result, null, null));
    }
}
