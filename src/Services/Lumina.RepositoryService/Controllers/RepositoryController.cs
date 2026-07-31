using Lumina.Shared.DTOs;
using Lumina.Shared.Errors;
using Lumina.Shared.Extensions;
using Lumina.Shared.Models;
using Lumina.Web.Shared.Authorization;
using Lumina.Web.Shared.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lumina.RepositoryService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // Defense-in-depth (see SecurityController): re-validate the JWT here
            // too, so a directly-reached internal port is not anonymous.
public class RepositoryController : ControllerBase
{
    private readonly Services.MinioStorageService _storage;
    private readonly Services.RepositoryManagerService _repoManager;
    private readonly Services.SignatureVerificationService _verification;
    private readonly Services.RepositoryPromotionService _promotions;
    private readonly ILogger<RepositoryController> _logger;

    public RepositoryController(Services.MinioStorageService storage, Services.RepositoryManagerService repoManager, Services.SignatureVerificationService verification, Services.RepositoryPromotionService promotions, ILogger<RepositoryController> logger)
    {
        _storage = storage;
        _repoManager = repoManager;
        _verification = verification;
        _promotions = promotions;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<RepositoryListResponse>>> ListRepositories()
    {
        var repos = await _storage.ListRepositoriesAsync();
        var response = new RepositoryListResponse(
            repos,
            repos.Count,
            Page: 1,
            PageSize: repos.Count);
        return Ok(new ApiResponse<RepositoryListResponse>(true, response, null, null));
    }

    [HttpPost]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<ApiResponse<PackageRepository>>> CreateRepository([FromBody] CreateRepositoryRequest request)
    {
        try
        {
            // SECURITY: allow-list basePath/arch (SEC-016) before they reach the
            // filesystem or any external process. Reject traversal / option-like
            // / out-of-charset values at the request boundary.
            ProcessArgumentSanitizer.ValidateRepositoryBasePath(request.BasePath);
            ProcessArgumentSanitizer.ValidateRepositoryArch(request.Arch);

            var repo = await _storage.CreateRepositoryAsync(
                request.Name, request.DisplayName, request.BasePath,
                request.Arch, request.Distribution, request.CreatedBy);
            return CreatedAtAction(nameof(ListRepositories), new ApiResponse<PackageRepository>(true, repo, null, "Repository created"));
        }
        catch (ArgumentException ex)
        {
            // BasePath/Arch allow-list validation (SEC-016). The message is
            // application-authored and safe to return.
            _logger.LogWarning(ex, "Rejected repository create (invalid basePath/arch)");
            return BadRequest(new ApiResponse<PackageRepository>(false, null, ex.Message, null));
        }
        catch (ConflictException ex)
        {
            // Duplicate Name — raised by the pre-check in MinioStorageService.
            return Conflict(new ApiResponse<PackageRepository>(false, null, ex.Message, null));
        }
        catch (DbUpdateException ex)
        {
            // Backstop for a create/create race that beats the duplicate-name
            // pre-check. Do NOT match on driver-specific "duplicate key" text —
            // log the full exception (it carries the SqlState) and return a
            // stable message so no DB internals cross the wire (SEC-022).
            _logger.LogError(ex, "Repository create failed DB update (possible duplicate name)");
            return Conflict(new ApiResponse<PackageRepository>(false, null, "A repository with this name already exists.", null));
        }
        catch (Exception ex)
        {
            return ApiResults.FromException<PackageRepository>(ex, _logger, "Repository.Create");
        }
    }

    [HttpPost("publish")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<ApiResponse<PackageResponse>>> PublishPackage([FromBody] PublishPackageRequest request)
    {
        try
        {
            var package = await _storage.PublishPackageAsync(request.ArtifactId, request.RepositoryId, request.PublishedBy);
            return Ok(new ApiResponse<PackageResponse>(true, PackageResponse.From(package), null, "Package published"));
        }
        catch (Exception ex)
        {
            // NotFoundException (repository) -> 404; ValidationException (no
            // signature) -> 400; other faults -> generic 500 (SEC-022).
            return ApiResults.FromException<PackageResponse>(ex, _logger, "Repository.PublishPackage", request.RepositoryId);
        }
    }

    /// <summary>
    /// Upload an RPM file directly to a repository.
    /// Accepts multipart/form-data with file, a detached PGP signature (.asc),
    /// and repositoryId. The signature is verified against the active public key
    /// before the package is published — unsigned/unverified RPMs are rejected.
    /// </summary>
    [HttpPost("upload")]
    [Authorize(Policy = AuthPolicies.Admin)]
    [RequestSizeLimit(500 * 1024 * 1024)] // 500MB limit
    public async Task<ActionResult<ApiResponse<PackageResponse>>> UploadPackage([FromForm] IFormFile file, [FromForm] IFormFile signature, [FromForm] Guid repositoryId, [FromForm] string? publishedBy)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<PackageResponse>(false, null, "No file uploaded", null));

        if (!file.FileName.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new ApiResponse<PackageResponse>(false, null, "Only .rpm files are allowed", null));

        if (signature == null || signature.Length == 0)
            return BadRequest(new ApiResponse<PackageResponse>(false, null, "A detached PGP signature (.asc) is required. Unsigned RPMs cannot be uploaded.", null));

        try
        {
            // Verify the detached signature against the active public key before
            // anything is written to the repository. Throws on failure.
            var verifiedSignature = await _verification.VerifyAsync(
                file.OpenReadStream(),
                signature.OpenReadStream(),
                file.FileName);

            var package = await _storage.UploadAndPublishPackageAsync(
                repositoryId,
                file.FileName,
                file.OpenReadStream(),
                file.Length,
                publishedBy ?? "upload",
                verifiedSignature);

            return Ok(new ApiResponse<PackageResponse>(true, PackageResponse.From(package), null, "Package uploaded, signature verified, and repository metadata updated"));
        }
        catch (Exception ex)
        {
            // ValidationException (signature missing/failed, repository
            // validation) -> 400; NotFoundException (repository) -> 404;
            // anything else -> generic 500 (SEC-022).
            return ApiResults.FromException<PackageResponse>(ex, _logger, "Repository.UploadPackage", repositoryId);
        }
    }

    [HttpPost("sync")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<ApiResponse<object>>> SyncRepository([FromBody] SyncRepositoryRequest request)
    {
        try
        {
            await _storage.SyncRepositoryAsync(request.RepositoryId);
            return Ok(new ApiResponse<object>(true, null, null, "Repository synced"));
        }
        catch (Exception ex)
        {
            // NotFoundException (repository) -> 404; other faults -> 500 (SEC-022).
            return ApiResults.FromException<object>(ex, _logger, "Repository.Sync", request.RepositoryId);
        }
    }

    [HttpGet("{repositoryId:guid}/packages")]
    public async Task<ActionResult<ApiResponse<List<PackageResponse>>>> ListPackages(Guid repositoryId)
    {
        var packages = await _storage.ListPackagesAsync(repositoryId);
        return Ok(new ApiResponse<List<PackageResponse>>(true, packages.Select(PackageResponse.From).ToList(), null, null));
    }

    [HttpPost("{repositoryId:guid}/promotions/{promotionSetId:guid}/rollback")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<ApiResponse<object>>> RollbackPromotion(
        Guid repositoryId,
        Guid promotionSetId,
        [FromBody] RollbackPromotionRequest request)
    {
        try
        {
            var actor = User.FindFirst("sub")?.Value ?? User.Identity?.Name
                ?? throw new ValidationException("Authenticated rollback actor is unavailable.");
            await _promotions.RollbackAsync(
                repositoryId, promotionSetId, actor, request.Reason, HttpContext.RequestAborted);
            return Ok(new ApiResponse<object>(true, null, null, "Promotion rolled back"));
        }
        catch (Exception exception)
        {
            return ApiResults.FromException<object>(
                exception, _logger, "Repository.RollbackPromotion", repositoryId);
        }
    }
}
