using System.Net.Http.Json;
using System.Text.Json;
using Lumina.Shared.DTOs;
using Lumina.Shared.Models.Enums;
using Lumina.SourceService.Data;
using Lumina.SourceService.Services;
using Lumina.Web.Shared.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Lumina.SourceService.Controllers;

[ApiController]
[Route("api/sources")]
[Authorize] // Defense-in-depth (see SecurityController): re-validate the JWT here
            // too, so a directly-reached internal port is not anonymous.
public class SourceController : ControllerBase
{
    private readonly ConfigParserService _configParser;
    private readonly SourceFetchQueue _fetchQueue;
    private readonly SourceStorageService _storageService;
    private readonly SourceUriValidator _uriValidator;
    private readonly SourceDbContext _db;
    private readonly ILogger<SourceController> _logger;
    private readonly IConfiguration _config;

    public SourceController(
        ConfigParserService configParser,
        SourceFetchQueue fetchQueue,
        SourceStorageService storageService,
        SourceUriValidator uriValidator,
        SourceDbContext db,
        ILogger<SourceController> logger,
        IConfiguration config)
    {
        _configParser = configParser;
        _fetchQueue = fetchQueue;
        _storageService = storageService;
        _uriValidator = uriValidator;
        _db = db;
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// List all packages from conf.ini with their fetch status
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<SourceListResponse>> ListSources()
    {
        var packages = _configParser.ParsePackages();

        // Get latest job for each package
        var jobs = await _db.SourceJobs
            .GroupBy(j => j.PackageName)
            .Select(g => g.OrderByDescending(j => j.CreatedAt).First())
            .ToListAsync();

        var response = packages.Select(pkg =>
        {
            var job = jobs.FirstOrDefault(j =>
                j.PackageName.Equals(pkg.Name, StringComparison.OrdinalIgnoreCase));

            return new SourcePackageResponse(
                pkg.Name,
                pkg.Source,
                pkg.SourceType,
                pkg.SourceBranch,
                job?.Status ?? SourceStatus.Pending,
                job?.ErrorMessage,
                job?.FileSize,
                job?.HashSha256,
                job?.FetchCompletedAt
            );
        }).ToList();

        return Ok(new ApiResponse<SourceListResponse>(true, new SourceListResponse(response, response.Count), null, null));
    }

    /// <summary>
    /// Get info about a specific package
    /// </summary>
    [HttpGet("{name}")]
    public async Task<ActionResult<ApiResponse<SourcePackageResponse>>> GetSource(string name)
    {
        var pkg = _configParser.GetPackage(name);
        if (pkg == null)
            return NotFound(new ApiResponse<SourcePackageResponse>(false, null, $"Package '{name}' not found in configuration", null));

        var job = await _db.SourceJobs
            .Where(j => j.PackageName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync();

        return Ok(new ApiResponse<SourcePackageResponse>(
            true,
            new SourcePackageResponse(
                pkg.Name,
                pkg.Source,
                pkg.SourceType,
                pkg.SourceBranch,
                job?.Status ?? SourceStatus.Pending,
                job?.ErrorMessage,
                job?.FileSize,
                job?.HashSha256,
                job?.FetchCompletedAt
            ),
            null,
            null));
    }

    /// <summary>
    /// Fetch sources for a specific package
    /// </summary>
    [HttpPost("{name}/fetch")]
    public async Task<ActionResult<ApiResponse<SourceFetchResponse>>> FetchSource(string name, [FromBody] FetchSourceRequest? request = null)
    {
        var pkg = _configParser.GetPackage(name);
        if (pkg == null)
            return NotFound(new ApiResponse<SourceFetchResponse>(false, null, $"Package '{name}' not found in configuration", null));

        try
        {
            // SECURITY: fail fast on unsafe sources (SSRF / arbitrary-file-read)
            // before kicking off a background fetch. SourceFetchService validates
            // again as defense-in-depth.
            await _uriValidator.ValidateAsync(pkg.Source, pkg.SourceType, pkg.SourceBranch);

            var job = await _fetchQueue.EnqueueAsync(
                pkg.Name,
                pkg.Source,
                pkg.SourceType,
                pkg.SourceBranch,
                request?.MaxRetries ?? 3);

            return Accepted(new ApiResponse<SourceFetchResponse>(
                true,
                new SourceFetchResponse(job.Id, job.PackageName, job.Status, job.ErrorMessage),
                null,
                null));
        }
        catch (SourceValidationException ex)
        {
            // Domain-authored message — safe to surface as the Error field.
            return BadRequest(new ApiResponse<SourceFetchResponse>(false, null, ex.Message, null));
        }
        catch (Exception ex)
        {
            // Never return ex.Message — it can contain DB/stack hints (SEC-022).
            return ApiResults.FromException<SourceFetchResponse>(ex, _logger, "Sources.FetchSource", name);
        }
    }

    /// <summary>
    /// Fetch all sources from config
    /// </summary>
    [HttpPost("fetch-all")]
    public async Task<ActionResult<ApiResponse<List<SourceFetchResponse>>>> FetchAllSources([FromBody] FetchAllSourcesRequest? request = null)
    {
        try
        {
            var jobs = await _fetchQueue.EnqueueAllAsync(_configParser, request?.MaxRetries ?? 3);

            var data = jobs.Select(j =>
                new SourceFetchResponse(j.Id, j.PackageName, j.Status, j.ErrorMessage)).ToList();

            return Accepted(new ApiResponse<List<SourceFetchResponse>>(true, data, null, null));
        }
        catch (Exception ex)
        {
            // Never return ex.Message — it can contain DB/stack hints (SEC-022).
            return ApiResults.FromException<List<SourceFetchResponse>>(ex, _logger, "Sources.FetchAllSources");
        }
    }

    /// <summary>
    /// Get the fetch status of a package
    /// </summary>
    [HttpGet("{name}/status")]
    public async Task<ActionResult<ApiResponse<SourceFetchResponse>>> GetSourceStatus(string name)
    {
        var job = await _db.SourceJobs
            .Where(j => j.PackageName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync();

        if (job == null)
            return NotFound(new ApiResponse<SourceFetchResponse>(false, null, $"No fetch job found for package '{name}'", null));

        return Ok(new ApiResponse<SourceFetchResponse>(
            true,
            new SourceFetchResponse(job.Id, job.PackageName, job.Status, job.ErrorMessage),
            null,
            null));
    }

    [HttpPost("jobs/{jobId:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<SourceFetchResponse>>> CancelFetch(Guid jobId)
    {
        var job = await _db.SourceJobs.SingleOrDefaultAsync(j => j.Id == jobId);
        if (job is null)
            return NotFound(new ApiResponse<SourceFetchResponse>(false, null, "Source fetch job not found.", null));

        if (job.Status is SourceStatus.Ready or SourceStatus.Failed or SourceStatus.Cancelled)
        {
            return Conflict(new ApiResponse<SourceFetchResponse>(
                false, null, $"Source fetch job is already {job.Status}.", null));
        }

        job.CancellationRequested = true;
        job.UpdatedAt = DateTime.UtcNow;
        if (job.Status == SourceStatus.Pending)
        {
            job.Status = SourceStatus.Cancelled;
            job.FetchCompletedAt = DateTime.UtcNow;
            job.ErrorMessage = "Cancelled by request.";
        }
        await _db.SaveChangesAsync();

        return Accepted(new ApiResponse<SourceFetchResponse>(
            true,
            new SourceFetchResponse(job.Id, job.PackageName, job.Status, job.ErrorMessage),
            null,
            "Cancellation requested."));
    }

    /// <summary>
    /// Get a download URL for a fetched source archive
    /// </summary>
    [HttpGet("{name}/download")]
    public async Task<ActionResult<ApiResponse<SourceDownloadResponse>>> DownloadSource(string name)
    {
        var pkg = _configParser.GetPackage(name);
        if (pkg == null)
            return NotFound(new ApiResponse<SourceDownloadResponse>(false, null, $"Package '{name}' not found in configuration", null));

        var job = await _db.SourceJobs
            .Where(j => j.PackageName.Equals(name, StringComparison.OrdinalIgnoreCase)
                && j.Status == SourceStatus.Ready)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync();

        if (job == null)
            return BadRequest(new ApiResponse<SourceDownloadResponse>(false, null, $"No ready source for package '{name}'. Fetch first.", null));

        try
        {
            var url = await _storageService.GetDownloadUrlAsync(name);
            if (url == null)
                return NotFound(new ApiResponse<SourceDownloadResponse>(false, null, "Source archive not found in storage", null));

            return Ok(new ApiResponse<SourceDownloadResponse>(
                true,
                new SourceDownloadResponse(url, name, job.FileSize, job.HashSha256),
                null,
                null));
        }
        catch (Exception ex)
        {
            // Never return ex.Message — it can contain DB/stack hints (SEC-022).
            return ApiResults.FromException<SourceDownloadResponse>(ex, _logger, "Sources.DownloadSource", name);
        }
    }

    /// <summary>
    /// Reload configuration from conf.ini
    /// </summary>
    [HttpPost("reload-config")]
    public ActionResult<ApiResponse<SourceConfigMutationResponse>> ReloadConfig()
    {
        var packages = _configParser.ParsePackages();
        return Ok(new ApiResponse<SourceConfigMutationResponse>(
            true,
            new SourceConfigMutationResponse($"Reloaded {packages.Count} packages from configuration", packages.Count),
            null,
            null));
    }
}
