using System.Net.Http.Json;
using System.Text.Json;
using Lumina.Shared.DTOs;
using Lumina.Shared.Events;
using Lumina.Shared.Models.Enums;
using Lumina.SourceService.Data;
using Lumina.SourceService.Services;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Lumina.SourceService.Controllers;

[ApiController]
[Route("api/sources")]
public class SourceController : ControllerBase
{
    private readonly ConfigParserService _configParser;
    private readonly SourceFetchService _fetchService;
    private readonly SourceStorageService _storageService;
    private readonly SourceDbContext _db;
    private readonly ILogger<SourceController> _logger;
    private readonly IConfiguration _config;
    private readonly IBus _bus;

    public SourceController(
        ConfigParserService configParser,
        SourceFetchService fetchService,
        SourceStorageService storageService,
        SourceDbContext db,
        ILogger<SourceController> logger,
        IConfiguration config,
        IBus bus)
    {
        _configParser = configParser;
        _fetchService = fetchService;
        _storageService = storageService;
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
    public async Task<ActionResult<SourcePackageResponse>> GetSource(string name)
    {
        var pkg = _configParser.GetPackage(name);
        if (pkg == null)
            return NotFound(new { error = $"Package '{name}' not found in configuration" });

        var job = await _db.SourceJobs
            .Where(j => j.PackageName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync();

        return Ok(new SourcePackageResponse(
            pkg.Name,
            pkg.Source,
            pkg.SourceType,
            pkg.SourceBranch,
            job?.Status ?? SourceStatus.Pending,
            job?.ErrorMessage,
            job?.FileSize,
            job?.HashSha256,
            job?.FetchCompletedAt
        ));
    }

    /// <summary>
    /// Fetch sources for a specific package
    /// </summary>
    [HttpPost("{name}/fetch")]
    public async Task<ActionResult<SourceFetchResponse>> FetchSource(string name, [FromBody] FetchSourceRequest? request = null)
    {
        var pkg = _configParser.GetPackage(name);
        if (pkg == null)
            return NotFound(new { error = $"Package '{name}' not found in configuration" });

        try
        {
            var job = await _fetchService.FetchAsync(
                pkg.Name,
                pkg.Source,
                pkg.SourceType,
                pkg.SourceBranch,
                request?.MaxRetries ?? 3);

            return Accepted(new SourceFetchResponse(job.Id, job.PackageName, job.Status, job.ErrorMessage));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start fetch for {Package}", name);
            return StatusCode(500, new { error = $"Failed to start fetch: {ex.Message}" });
        }
    }

    /// <summary>
    /// Fetch all sources from config
    /// </summary>
    [HttpPost("fetch-all")]
    public async Task<ActionResult<List<SourceFetchResponse>>> FetchAllSources([FromBody] FetchAllSourcesRequest? request = null)
    {
        try
        {
            var jobs = await _fetchService.FetchAllAsync(_configParser, request?.MaxRetries ?? 3);

            return Accepted(jobs.Select(j =>
                new SourceFetchResponse(j.Id, j.PackageName, j.Status, j.ErrorMessage)).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start fetch-all");
            return StatusCode(500, new { error = $"Failed to start fetch: {ex.Message}" });
        }
    }

    /// <summary>
    /// Get the fetch status of a package
    /// </summary>
    [HttpGet("{name}/status")]
    public async Task<ActionResult<SourceFetchResponse>> GetSourceStatus(string name)
    {
        var job = await _db.SourceJobs
            .Where(j => j.PackageName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync();

        if (job == null)
            return NotFound(new { error = $"No fetch job found for package '{name}'" });

        return Ok(new SourceFetchResponse(job.Id, job.PackageName, job.Status, job.ErrorMessage));
    }

    /// <summary>
    /// Get a download URL for a fetched source archive
    /// </summary>
    [HttpGet("{name}/download")]
    public async Task<ActionResult> DownloadSource(string name)
    {
        var pkg = _configParser.GetPackage(name);
        if (pkg == null)
            return NotFound(new { error = $"Package '{name}' not found in configuration" });

        var job = await _db.SourceJobs
            .Where(j => j.PackageName.Equals(name, StringComparison.OrdinalIgnoreCase)
                && j.Status == SourceStatus.Ready)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync();

        if (job == null)
            return BadRequest(new { error = $"No ready source for package '{name}'. Fetch first." });

        try
        {
            var url = await _storageService.GetDownloadUrlAsync(name);
            if (url == null)
                return NotFound(new { error = "Source archive not found in storage" });

            return Ok(new { url, packageName = name, fileSize = job.FileSize, hashSha256 = job.HashSha256 });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate download URL for {Package}", name);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Fetch sources and trigger RPM build for a package from conf.ini.
    /// Reads conf.ini for source info, fetches, creates tarball, then calls BuildService.
    /// </summary>
    [HttpPost("{name}/build")]
    public async Task<ActionResult> BuildPackage(string name, [FromBody] BuildFromConfigRequest? request = null)
    {
        var pkg = _configParser.GetPackage(name);
        if (pkg == null)
            return NotFound(new { error = $"Package '{name}' not found in configuration" });

        try
        {
            // 1. Find spec content — look in /app/specs/{name}.*, /app/Test/{name}.*, or use request
            var specContent = request?.SpecContent;
            var specName = request?.SpecName ?? $"{name}.spec";

            if (string.IsNullOrEmpty(specContent))
            {
                // Try to find spec file in known locations
                var specSearchPaths = new[]
                {
                    $"/app/specs/{name}.spec",
                    $"/app/specs/{name}.txt",
                    $"/app/Test/{name}.txt",
                    $"/app/specs/test_package.txt",
                    $"/app/Test/test_package.txt"
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
                    return BadRequest(new { error = $"No spec file found for package '{name}'. Provide SpecContent in request or place spec file in /app/specs/" });
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
            return Ok(new { message = $"Build triggered for {name}", package = name, version = packageVersion });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build package {Package}", name);
            return StatusCode(500, new { error = $"Build failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Get the raw conf.ini content
    /// </summary>
    [HttpGet("config")]
    public ActionResult GetConfig()
    {
        var content = _configParser.GetRawContent();
        return Ok(new { content });
    }

    /// <summary>
    /// Save the full conf.ini content
    /// </summary>
    [HttpPut("config")]
    public ActionResult SaveConfig([FromBody] UpdateConfigRequest request)
    {
        try
        {
            _configParser.SaveContent(request.Content);
            var packages = _configParser.ParsePackages();
            return Ok(new { message = $"Saved config with {packages.Count} packages", count = packages.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save config");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Add a new package to conf.ini
    /// </summary>
    [HttpPost("config/package")]
    public async Task<ActionResult> AddPackageToConfig([FromBody] AddPackageToConfigRequest request)
    {
        try
        {
            var existing = _configParser.GetPackage(request.Name);
            if (existing != null)
                return BadRequest(new { error = $"Package '{request.Name}' already exists in configuration" });

            _configParser.AddPackage(new Services.PackageSourceConfig
            {
                Name = request.Name,
                Source = request.Source,
                SourceType = Enum.TryParse<Shared.Models.Enums.SourceType>(request.SourceType, true, out var st)
                    ? st : Shared.Models.Enums.SourceType.Http,
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

            return Ok(new { message = $"Package '{request.Name}' added to configuration" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add package to config");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Remove a package from conf.ini
    /// </summary>
    [HttpDelete("config/{name}")]
    public ActionResult RemovePackageFromConfig(string name)
    {
        try
        {
            var removed = _configParser.RemovePackage(name);
            if (!removed)
                return NotFound(new { error = $"Package '{name}' not found in configuration" });

            return Ok(new { message = $"Package '{name}' removed from configuration" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove package from config");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Reload configuration from conf.ini
    /// </summary>
    [HttpPost("reload-config")]
    public ActionResult ReloadConfig()
    {
        _configParser.ParsePackages(); // Forces re-read
        var packages = _configParser.ParsePackages();
        return Ok(new { message = $"Reloaded {packages.Count} packages from configuration", count = packages.Count });
    }
}

public record BuildFromConfigRequest(
    string? SpecContent = null,
    string? SpecName = null,
    string? BuildImage = null);
