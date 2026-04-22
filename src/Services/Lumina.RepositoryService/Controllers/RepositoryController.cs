using Lumina.Shared.DTOs;
using Lumina.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace Lumina.RepositoryService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RepositoryController : ControllerBase
{
    private readonly Services.MinioStorageService _storage;
    private readonly ILogger<RepositoryController> _logger;

    public RepositoryController(Services.MinioStorageService storage, ILogger<RepositoryController> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<PackageRepository>>>> ListRepositories()
    {
        var repos = await _storage.ListRepositoriesAsync();
        return Ok(new ApiResponse<List<PackageRepository>>(true, repos, null, null));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<PackageRepository>>> CreateRepository([FromBody] CreateRepositoryRequest request)
    {
        try
        {
            var repo = await _storage.CreateRepositoryAsync(
                request.Name, request.DisplayName, request.BasePath,
                request.Arch, request.Distribution, request.CreatedBy);
            return CreatedAtAction(nameof(ListRepositories), new ApiResponse<PackageRepository>(true, repo, null, "Repository created"));
        }
        catch (Exception ex) when (ex.InnerException?.Message?.Contains("duplicate key") == true ||
                                    ex.Message?.Contains("duplicate key") == true)
        {
            return Conflict(new ApiResponse<PackageRepository>(false, null, "Repository with this name already exists", null));
        }
    }

    [HttpPost("publish")]
    public async Task<ActionResult<ApiResponse<Package>>> PublishPackage([FromBody] PublishPackageRequest request)
    {
        try
        {
            var package = await _storage.PublishPackageAsync(request.ArtifactId, request.RepositoryId, request.PublishedBy);
            return Ok(new ApiResponse<Package>(true, package, null, "Package published"));
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<Package>(false, null, ex.Message, null));
        }
    }

    [HttpPost("sync")]
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
    public async Task<ActionResult<ApiResponse<List<Package>>>> ListPackages(Guid repositoryId)
    {
        var packages = await _storage.ListPackagesAsync(repositoryId);
        return Ok(new ApiResponse<List<Package>>(true, packages, null, null));
    }
}