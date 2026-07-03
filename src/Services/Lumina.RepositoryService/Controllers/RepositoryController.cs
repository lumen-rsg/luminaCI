using Lumina.Shared.DTOs;
using Lumina.Shared.Extensions;
using Lumina.Shared.Models;
using Lumina.Web.Shared.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    private readonly ILogger<RepositoryController> _logger;

    public RepositoryController(Services.MinioStorageService storage, Services.RepositoryManagerService repoManager, Services.SignatureVerificationService verification, ILogger<RepositoryController> logger)
    {
        _storage = storage;
        _repoManager = repoManager;
        _verification = verification;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<PackageRepository>>>> ListRepositories()
    {
        var repos = await _storage.ListRepositoriesAsync();
        return Ok(new ApiResponse<List<PackageRepository>>(true, repos, null, null));
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
            return BadRequest(new ApiResponse<PackageRepository>(false, null, ex.Message, null));
        }
        catch (Exception ex) when (ex.InnerException?.Message?.Contains("duplicate key") == true ||
                                    ex.Message?.Contains("duplicate key") == true)
        {
            return Conflict(new ApiResponse<PackageRepository>(false, null, "Repository with this name already exists", null));
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
            return BadRequest(new ApiResponse<PackageResponse>(false, null, ex.Message, null));
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
            _logger.LogError(ex, "Failed to upload package to repository {RepoId}", repositoryId);
            return BadRequest(new ApiResponse<PackageResponse>(false, null, ex.Message, null));
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
            return BadRequest(new ApiResponse<object>(false, null, ex.Message, null));
        }
    }

    [HttpGet("{repositoryId:guid}/packages")]
    public async Task<ActionResult<ApiResponse<List<PackageResponse>>>> ListPackages(Guid repositoryId)
    {
        var packages = await _storage.ListPackagesAsync(repositoryId);
        return Ok(new ApiResponse<List<PackageResponse>>(true, packages.Select(PackageResponse.From).ToList(), null, null));
    }
}