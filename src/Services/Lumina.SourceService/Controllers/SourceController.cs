using System.Net.Http.Json;
using System.Text.Json;
using Lumina.Shared.DTOs;
using Lumina.Shared.Events;
using Lumina.Shared.Models.Enums;
using Lumina.SourceService.Data;
using Lumina.SourceService.Services;
using Lumina.Web.Shared.Authorization;
using Lumina.Web.Shared.Errors;
using MassTransit;
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
    private readonly SourceFetchService _fetchService;
    private readonly SourceStorageService _storageService;
    private readonly SourceUriValidator _uriValidator;
    private readonly SourceDbContext _db;
    private readonly ILogger<SourceController> _logger;
    private readonly IConfiguration _config;
    private readonly IBus _bus;

    public SourceController(
        ConfigParserService configParser,
        SourceFetchService fetchService,
        SourceStorageService storageService,
        SourceUriValidator uriValidator,
        SourceDbContext db,
        ILogger<SourceController> logger,
        IConfiguration config,
        IBus bus)
    {
        _configParser = configParser;
        _fetchService = fetchService;
        _storageService = storageService;
        _uriValidator = uriValidator;
        _db = db;
        _logger = logger;
        _config = config;
        _bus = bus;
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

            var job = await _fetchService.FetchAsync(
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
            var jobs = await _fetchService.FetchAllAsync(_configParser, request?.MaxRetries ?? 3);

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
    /// Fetch sources and trigger RPM build for a package from conf.ini.
    /// Reads conf.ini for source info, fetches, creates tarball, then calls BuildService.
    /// </summary>
    [HttpPost("{name}/build")]
    public async Task<ActionResult<ApiResponse<object>>> BuildPackage(string name, [FromBody] BuildFromConfigRequest? request = null)
    {
        var pkg = _configParser.GetPackage(name);
        if (pkg == null)
            return NotFound(new ApiResponse<object>(false, null, $"Package '{name}' not found in configuration", null));

        try
        {
            // SECURITY: validate the configured source before fetch/build.
            await _uriValidator.ValidateAsync(pkg.Source, pkg.SourceType, pkg.SourceBranch);

            // 1. Find spec content — look in /app/specs/{name}.*, or use request
            var specContent = request?.SpecContent;
            var specName = request?.SpecName ?? $"{name}.spec";

            if (string.IsNullOrEmpty(specContent))
            {
                // Try to find spec file in known locations. Spec files must be
                // supplied via the request body or placed under /app/specs/ —
                // never load test/dev fixtures from the runtime image.
                var specSearchPaths = new[]
                {
                    $"/app/specs/{name}.spec",
                    $"/app/specs/{name}.txt",
                    $"/app/specs/test_package.txt"
                };

                foreach (var specPath in specSearchPaths)
                {
                    if (System.IO.File.Exists(specPath))
                    {
                        specContent = await System.IO.File.ReadAllTextAsync(specPath);
                        _logger.LogInformation("Found spec file at {Path}", specPath);
                        break;
                    }
                }

                if (string.IsNullOrEmpty(specContent))
                    return BadRequest(new ApiResponse<object>(false, null, $"No spec file found for package '{name}'. Provide SpecContent in request or place spec file in /app/specs/", null));
            }

            // 2. Extract version from spec (Version: X.Y)
            var versionLine = specContent.Split('\n')
                .FirstOrDefault(l => l.TrimStart().StartsWith("Version:", StringComparison.OrdinalIgnoreCase));
            var packageVersion = versionLine?.Split(':', 2).LastOrDefault()?.Trim()
                ?? "1.0";

            _logger.LogInformation("Building package {Package} v{Version} from conf.ini", name, packageVersion);

            // 3. Fetch sources and prepare RPM-compatible tarball
            var prepareResult = await _fetchService.FetchAndPrepareForBuildAsync(
                name, pkg.Source, pkg.SourceType, pkg.SourceBranch,
                packageVersion, specContent);

            // 4. Publish BuildTriggerFromConfig event via MassTransit
            await _bus.Publish(new BuildTriggerFromConfig(
                name,
                prepareResult.SourceDir,
                specContent,
                specName,
                pkg.BuildImage ?? request?.BuildImage,
                "source-service"
            ));

            _logger.LogInformation("Published BuildTriggerFromConfig for package {Package}", name);
            return Ok(new ApiResponse<object>(true, new { package = name, version = packageVersion }, null, $"Build triggered for {name}"));
        }
        catch (SourceValidationException ex)
        {
            // Domain-authored message — safe to surface as the Error field.
            return BadRequest(new ApiResponse<object>(false, null, ex.Message, null));
        }
        catch (Exception ex)
        {
            // Never return ex.Message — it can contain DB/stack hints (SEC-022).
            return ApiResults.FromException<object>(ex, _logger, "Sources.BuildPackage", name);
        }
    }

    /// <summary>
    /// Get the raw conf.ini content
    /// </summary>
    [HttpGet("config")]
    public ActionResult<ApiResponse<SourceConfigResponse>> GetConfig()
    {
        var content = _configParser.GetRawContent();
        return Ok(new ApiResponse<SourceConfigResponse>(true, new SourceConfigResponse(content), null, null));
    }

    /// <summary>
    /// Save the full conf.ini content
    /// </summary>
    [HttpPut("config")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public ActionResult<ApiResponse<SourceConfigMutationResponse>> SaveConfig([FromBody] UpdateConfigRequest request)
    {
        try
        {
            _configParser.SaveContent(request.Content);
            var packages = _configParser.ParsePackages();
            return Ok(new ApiResponse<SourceConfigMutationResponse>(
                true,
                new SourceConfigMutationResponse($"Saved config with {packages.Count} packages", packages.Count),
                null,
                null));
        }
        catch (Exception ex)
        {
            // Never return ex.Message — it can contain DB/stack hints (SEC-022).
            return ApiResults.FromException<SourceConfigMutationResponse>(ex, _logger, "Sources.SaveConfig");
        }
    }

    /// <summary>
    /// Add a new package to conf.ini
    /// </summary>
    [HttpPost("config/package")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<ApiResponse<SourceConfigMutationResponse>>> AddPackageToConfig([FromBody] AddPackageToConfigRequest request)
    {
        try
        {
            // SECURITY: reject unsafe sources (SSRF / arbitrary-file-read) at
            // registration time so they can never be persisted to conf.ini and
            // later fetched. SourceFetchService still validates as a backstop.
            var sourceType = Enum.TryParse<Shared.Models.Enums.SourceType>(request.SourceType, true, out var st)
                ? st : Shared.Models.Enums.SourceType.Http;
            await _uriValidator.ValidateAsync(request.Source, sourceType, request.SourceBranch);

            var existing = _configParser.GetPackage(request.Name);
            if (existing != null)
                return BadRequest(new ApiResponse<SourceConfigMutationResponse>(false, null, $"Package '{request.Name}' already exists in configuration", null));

            _configParser.AddPackage(new Services.PackageSourceConfig
            {
                Name = request.Name,
                Source = request.Source,
                SourceType = sourceType,
                SourceBranch = request.SourceBranch,
                BuildImage = request.BuildImage
            });

            // Save spec content to file if provided
            if (!string.IsNullOrWhiteSpace(request.SpecContent))
            {
                var specDir = "/app/specs";
                if (!System.IO.Directory.Exists(specDir))
                    System.IO.Directory.CreateDirectory(specDir);
                var specPath = System.IO.Path.Combine(specDir, $"{request.Name}.spec");
                await System.IO.File.WriteAllTextAsync(specPath, request.SpecContent);
                _logger.LogInformation("Saved spec file for {Name} to {Path}", request.Name, specPath);
            }

            return Ok(new ApiResponse<SourceConfigMutationResponse>(
                true,
                new SourceConfigMutationResponse($"Package '{request.Name}' added to configuration"),
                null,
                null));
        }
        catch (SourceValidationException ex)
        {
            // Domain-authored message — safe to surface as the Error field.
            return BadRequest(new ApiResponse<SourceConfigMutationResponse>(false, null, ex.Message, null));
        }
        catch (Exception ex)
        {
            // Never return ex.Message — it can contain DB/stack hints (SEC-022).
            return ApiResults.FromException<SourceConfigMutationResponse>(ex, _logger, "Sources.AddPackageToConfig", request.Name);
        }
    }

    /// <summary>
    /// Remove a package from conf.ini
    /// </summary>
    [HttpDelete("config/{name}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public ActionResult<ApiResponse<SourceConfigMutationResponse>> RemovePackageFromConfig(string name)
    {
        try
        {
            var removed = _configParser.RemovePackage(name);
            if (!removed)
                return NotFound(new ApiResponse<SourceConfigMutationResponse>(false, null, $"Package '{name}' not found in configuration", null));

            return Ok(new ApiResponse<SourceConfigMutationResponse>(
                true,
                new SourceConfigMutationResponse($"Package '{name}' removed from configuration"),
                null,
                null));
        }
        catch (Exception ex)
        {
            // Never return ex.Message — it can contain DB/stack hints (SEC-022).
            return ApiResults.FromException<SourceConfigMutationResponse>(ex, _logger, "Sources.RemovePackageFromConfig", name);
        }
    }

    /// <summary>
    /// Reload configuration from conf.ini
    /// </summary>
    [HttpPost("reload-config")]
    public ActionResult<ApiResponse<SourceConfigMutationResponse>> ReloadConfig()
    {
        _configParser.ParsePackages(); // Forces re-read
        var packages = _configParser.ParsePackages();
        return Ok(new ApiResponse<SourceConfigMutationResponse>(
            true,
            new SourceConfigMutationResponse($"Reloaded {packages.Count} packages from configuration", packages.Count),
            null,
            null));
    }
}

public record BuildFromConfigRequest(
    string? SpecContent = null,
    string? SpecName = null,
    string? BuildImage = null);
