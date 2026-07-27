using System.Security.Claims;
using Lumina.Shared.DTOs;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Lumina.SourceService.Data;
using Lumina.SourceService.Services;
using Lumina.Web.Shared.Authorization;
using Lumina.Web.Shared.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lumina.SourceService.Controllers;

[ApiController]
[Route("api/sources")]
[Authorize]
public class SourceController : ControllerBase
{
    private readonly PackageCatalogService _catalog;
    private readonly SourceFetchQueue _fetchQueue;
    private readonly SourceStorageService _storageService;
    private readonly SourceUriValidator _uriValidator;
    private readonly SourceDbContext _db;
    private readonly ILogger<SourceController> _logger;

    public SourceController(
        PackageCatalogService catalog,
        SourceFetchQueue fetchQueue,
        SourceStorageService storageService,
        SourceUriValidator uriValidator,
        SourceDbContext db,
        ILogger<SourceController> logger)
    {
        _catalog = catalog;
        _fetchQueue = fetchQueue;
        _storageService = storageService;
        _uriValidator = uriValidator;
        _db = db;
        _logger = logger;
    }

    /// <summary>List enabled database-backed package definitions.</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<SourceListResponse>>> ListSources(
        CancellationToken cancellationToken)
    {
        var packages = await _catalog.ListAsync(cancellationToken);
        var packageIds = packages.Select(package => package.Id).ToList();
        var jobs = await _db.SourceJobs
            .AsNoTracking()
            .Where(job => job.PackageRevisionId != null &&
                          packageIds.Contains(job.PackageRevision!.PackageDefinitionId))
            .Include(job => job.PackageRevision)
            .OrderByDescending(job => job.CreatedAt)
            .ToListAsync(cancellationToken);
        var latestJobs = jobs
            .GroupBy(job => job.PackageRevision!.PackageDefinitionId)
            .ToDictionary(group => group.Key, group => group.First());

        var response = packages
            .Select(package => ToResponse(
                package,
                latestJobs.GetValueOrDefault(package.Id)))
            .ToList();
        return Ok(new ApiResponse<SourceListResponse>(
            true, new SourceListResponse(response, response.Count), null, null));
    }

    /// <summary>Get the active immutable revision for one package.</summary>
    [HttpGet("{name}")]
    public async Task<ActionResult<ApiResponse<SourcePackageResponse>>> GetSource(
        string name,
        CancellationToken cancellationToken)
    {
        if (!PackageSourcePolicy.TryNormalizeSlug(name, out var normalized))
            return BadRequest(new ApiResponse<SourcePackageResponse>(
                false, null, "Package name is not a valid slug.", null));
        var package = await _catalog.GetAsync(normalized, cancellationToken: cancellationToken);
        if (package is null)
            return NotFound(new ApiResponse<SourcePackageResponse>(
                false, null, $"Package '{name}' was not found.", null));

        var job = await LatestJobAsync(package.Id, cancellationToken);
        return Ok(new ApiResponse<SourcePackageResponse>(
            true, ToResponse(package, job), null, null));
    }

    /// <summary>
    /// Create a package definition and automatically queue revision 1.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<ApiResponse<SourcePackageMutationResponse>>> CreatePackage(
        [FromBody] SavePackageSourceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var validated = await ValidateRequestAsync(request, cancellationToken);
            var (package, job) = await _catalog.CreateAsync(
                validated, Actor(), cancellationToken);
            var response = new SourcePackageMutationResponse(
                ToResponse(package, job), job is null ? null : ToFetchResponse(job));
            return CreatedAtAction(
                nameof(GetSource),
                new { name = package.Slug },
                new ApiResponse<SourcePackageMutationResponse>(
                    true, response, null,
                    job is null ? "Package created." : "Package created and source fetch queued."));
        }
        catch (SourceValidationException ex)
        {
            return BadRequest(new ApiResponse<SourcePackageMutationResponse>(
                false, null, ex.Message, null));
        }
        catch (PackageConflictException ex)
        {
            return Conflict(new ApiResponse<SourcePackageMutationResponse>(
                false, null, ex.Message, null));
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Concurrent package creation conflict for {Slug}", request.Slug);
            return Conflict(new ApiResponse<SourcePackageMutationResponse>(
                false, null, "A package with this slug already exists.", null));
        }
        catch (Exception ex)
        {
            return ApiResults.FromException<SourcePackageMutationResponse>(
                ex, _logger, "Sources.CreatePackage", request.Slug);
        }
    }

    /// <summary>
    /// Append an immutable revision and automatically queue that exact revision.
    /// ExpectedRevision prevents lost updates.
    /// </summary>
    [HttpPut("{name}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<ApiResponse<SourcePackageMutationResponse>>> UpdatePackage(
        string name,
        [FromBody] SavePackageSourceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var validated = await ValidateRequestAsync(request, cancellationToken);
            var (package, job) = await _catalog.UpdateAsync(
                name, validated, Actor(), cancellationToken);
            return Ok(new ApiResponse<SourcePackageMutationResponse>(
                true,
                new SourcePackageMutationResponse(
                    ToResponse(package, job),
                    job is null ? null : ToFetchResponse(job)),
                null,
                job is null ? "Package updated." : "Package revision saved and source fetch queued."));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new ApiResponse<SourcePackageMutationResponse>(
                false, null, $"Package '{name}' was not found.", null));
        }
        catch (SourceValidationException ex)
        {
            return BadRequest(new ApiResponse<SourcePackageMutationResponse>(
                false, null, ex.Message, null));
        }
        catch (PackageConflictException ex)
        {
            return Conflict(new ApiResponse<SourcePackageMutationResponse>(
                false, null, ex.Message, null));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new ApiResponse<SourcePackageMutationResponse>(
                false, null, "The package changed while it was being updated. Refresh and retry.", null));
        }
        catch (Exception ex)
        {
            return ApiResults.FromException<SourcePackageMutationResponse>(
                ex, _logger, "Sources.UpdatePackage", name);
        }
    }

    /// <summary>Disable a package without deleting its revision or fetch history.</summary>
    [HttpDelete("{name}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<ApiResponse<object>>> DisablePackage(
        string name,
        [FromQuery] int expectedRevision,
        CancellationToken cancellationToken)
    {
        try
        {
            await _catalog.DisableAsync(name, expectedRevision, cancellationToken);
            return Ok(new ApiResponse<object>(true, null, null, "Package disabled."));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new ApiResponse<object>(
                false, null, $"Package '{name}' was not found.", null));
        }
        catch (PackageConflictException ex)
        {
            return Conflict(new ApiResponse<object>(false, null, ex.Message, null));
        }
        catch (SourceValidationException ex)
        {
            return BadRequest(new ApiResponse<object>(false, null, ex.Message, null));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new ApiResponse<object>(
                false, null, "The package changed while it was being disabled. Refresh and retry.", null));
        }
    }

    /// <summary>Queue the active revision of a package for source resolution.</summary>
    [HttpPost("{name}/fetch")]
    public async Task<ActionResult<ApiResponse<SourceFetchResponse>>> FetchSource(
        string name,
        [FromBody] FetchSourceOptionsRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        if (!PackageSourcePolicy.TryNormalizeSlug(name, out var normalized))
            return BadRequest(new ApiResponse<SourceFetchResponse>(
                false, null, "Package name is not a valid slug.", null));
        var package = await _catalog.GetAsync(normalized, cancellationToken: cancellationToken);
        if (package is null || !package.IsEnabled)
            return NotFound(new ApiResponse<SourceFetchResponse>(
                false, null, $"Enabled package '{name}' was not found.", null));

        try
        {
            var revision = package.Revisions.Single(item =>
                item.RevisionNumber == package.ActiveRevisionNumber);
            await _uriValidator.ValidateAsync(
                revision.SourceUrl,
                revision.SourceType,
                revision.SourceReference,
                cancellationToken);
            var job = _fetchQueue.CreatePending(
                revision, package.Slug, request?.MaxRetries);
            _db.SourceJobs.Add(job);
            await _db.SaveChangesAsync(cancellationToken);
            return Accepted(new ApiResponse<SourceFetchResponse>(
                true, ToFetchResponse(job), null, null));
        }
        catch (SourceValidationException ex)
        {
            return BadRequest(new ApiResponse<SourceFetchResponse>(
                false, null, ex.Message, null));
        }
        catch (Exception ex)
        {
            return ApiResults.FromException<SourceFetchResponse>(
                ex, _logger, "Sources.FetchSource", name);
        }
    }

    /// <summary>Queue every enabled package's active immutable revision.</summary>
    [HttpPost("fetch-all")]
    public async Task<ActionResult<ApiResponse<List<SourceFetchResponse>>>> FetchAllSources(
        [FromBody] FetchAllSourcesRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var jobs = await _fetchQueue.EnqueueAllAsync(
                request?.MaxRetries, cancellationToken);
            return Accepted(new ApiResponse<List<SourceFetchResponse>>(
                true, jobs.Select(ToFetchResponse).ToList(), null, null));
        }
        catch (Exception ex)
        {
            return ApiResults.FromException<List<SourceFetchResponse>>(
                ex, _logger, "Sources.FetchAllSources");
        }
    }

    [HttpGet("{name}/status")]
    public async Task<ActionResult<ApiResponse<SourceFetchResponse>>> GetSourceStatus(
        string name,
        CancellationToken cancellationToken)
    {
        if (!PackageSourcePolicy.TryNormalizeSlug(name, out var normalized))
            return BadRequest(new ApiResponse<SourceFetchResponse>(
                false, null, "Package name is not a valid slug.", null));
        var job = await _db.SourceJobs
            .AsNoTracking()
            .Where(item => item.PackageName == normalized)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
            return NotFound(new ApiResponse<SourceFetchResponse>(
                false, null, $"No fetch job found for package '{name}'.", null));
        return Ok(new ApiResponse<SourceFetchResponse>(
            true, ToFetchResponse(job), null, null));
    }

    [HttpPost("jobs/{jobId:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<SourceFetchResponse>>> CancelFetch(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var job = await _db.SourceJobs.SingleOrDefaultAsync(
            item => item.Id == jobId, cancellationToken);
        if (job is null)
            return NotFound(new ApiResponse<SourceFetchResponse>(
                false, null, "Source fetch job not found.", null));
        if (job.Status is SourceStatus.Ready or SourceStatus.Failed or SourceStatus.Cancelled)
            return Conflict(new ApiResponse<SourceFetchResponse>(
                false, null, $"Source fetch job is already {job.Status}.", null));

        job.CancellationRequested = true;
        job.UpdatedAt = DateTime.UtcNow;
        if (job.Status == SourceStatus.Pending)
        {
            job.Status = SourceStatus.Cancelled;
            job.FetchCompletedAt = DateTime.UtcNow;
            job.ErrorMessage = "Cancelled by request.";
        }
        await _db.SaveChangesAsync(cancellationToken);
        return Accepted(new ApiResponse<SourceFetchResponse>(
            true, ToFetchResponse(job), null, "Cancellation requested."));
    }

    [HttpGet("{name}/download")]
    public async Task<ActionResult<ApiResponse<SourceDownloadResponse>>> DownloadSource(
        string name,
        CancellationToken cancellationToken)
    {
        if (!PackageSourcePolicy.TryNormalizeSlug(name, out var normalized))
            return BadRequest(new ApiResponse<SourceDownloadResponse>(
                false, null, "Package name is not a valid slug.", null));
        var package = await _catalog.GetAsync(normalized, cancellationToken: cancellationToken);
        if (package is null)
            return NotFound(new ApiResponse<SourceDownloadResponse>(
                false, null, $"Package '{name}' was not found.", null));

        var job = await _db.SourceJobs
            .AsNoTracking()
            .Where(item => item.PackageRevisionId != null &&
                           item.PackageRevision!.PackageDefinitionId == package.Id &&
                           item.Status == SourceStatus.Ready)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
            return BadRequest(new ApiResponse<SourceDownloadResponse>(
                false, null, $"No ready source for package '{name}'. Fetch first.", null));

        try
        {
            if (string.IsNullOrWhiteSpace(job.StoragePath) ||
                !await _storageService.ExistsAsync(job.StoragePath))
            {
                return NotFound(new ApiResponse<SourceDownloadResponse>(
                    false, null, "Source archive not found in storage.", null));
            }
            var url = await _storageService.GetDownloadUrlAsync(job.StoragePath);
            return Ok(new ApiResponse<SourceDownloadResponse>(
                true,
                new SourceDownloadResponse(
                    url, package.Slug, job.FileSize, job.HashSha256),
                null,
                null));
        }
        catch (Exception ex)
        {
            return ApiResults.FromException<SourceDownloadResponse>(
                ex, _logger, "Sources.DownloadSource", name);
        }
    }

    private async Task<SavePackageSourceRequest> ValidateRequestAsync(
        SavePackageSourceRequest request,
        CancellationToken cancellationToken)
    {
        PackageSourcePolicy.Validate(request);
        var validated = await _uriValidator.ValidateAsync(
            request.SourceUrl,
            request.SourceType,
            request.SourceReference,
            cancellationToken);
        return request with
        {
            Slug = PackageSourcePolicy.NormalizeSlug(request.Slug),
            SourceUrl = validated.Url,
            SourceReference = validated.Branch,
            ExpectedSha256 = request.ExpectedSha256?.ToLowerInvariant()
        };
    }

    private async Task<SourceJob?> LatestJobAsync(
        Guid packageId,
        CancellationToken cancellationToken)
        => await _db.SourceJobs
            .AsNoTracking()
            .Where(job => job.PackageRevisionId != null &&
                          job.PackageRevision!.PackageDefinitionId == packageId)
            .OrderByDescending(job => job.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private static SourcePackageResponse ToResponse(
        PackageDefinition package,
        SourceJob? job)
    {
        var revision = package.Revisions.Single(item =>
            item.RevisionNumber == package.ActiveRevisionNumber);
        return new SourcePackageResponse(
            package.Id,
            package.Slug,
            revision.RevisionNumber,
            package.IsEnabled,
            revision.SourceUrl,
            revision.SourceType,
            revision.SourceReference,
            revision.ExpectedSha256,
            revision.SpecPath,
            revision.BuildImage,
            job?.Status ?? SourceStatus.Pending,
            job?.ErrorMessage,
            job?.FileSize,
            job?.HashSha256,
            job?.FetchCompletedAt,
            job?.ResolvedRevision,
            job?.ResolvedUrl);
    }

    private static SourceFetchResponse ToFetchResponse(SourceJob job)
        => new(
            job.Id,
            job.PackageName,
            job.Status,
            job.ErrorMessage,
            job.ResolvedRevision,
            job.ResolvedUrl);

    private string Actor()
        => User.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? User.Identity?.Name
           ?? "unknown";
}
