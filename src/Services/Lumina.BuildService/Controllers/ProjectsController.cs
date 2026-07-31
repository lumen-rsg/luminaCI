using Lumina.BuildService.Services;
using Lumina.Shared.DTOs;
using Lumina.Shared.Models;
using Lumina.Web.Shared.Authorization;
using Lumina.Web.Shared.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lumina.BuildService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class ProjectsController : ControllerBase
{
    private readonly BuildProjectService _projects;
    private readonly ILogger<ProjectsController> _logger;

    public ProjectsController(
        BuildProjectService projects,
        ILogger<ProjectsController> logger)
    {
        _projects = projects;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<BuildProjectListResponse>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var normalizedPage = Math.Max(1, page);
            var normalizedPageSize = Math.Clamp(pageSize, 1, 100);
            var (projects, totalCount) = await _projects.ListAsync(
                normalizedPage, normalizedPageSize, cancellationToken);
            return Ok(new ApiResponse<BuildProjectListResponse>(
                true,
                new BuildProjectListResponse(
                    projects.Select(ToResponse).ToList(),
                    totalCount,
                    normalizedPage,
                    normalizedPageSize),
                null,
                null));
        }
        catch (Exception exception)
        {
            return ApiResults.FromException<BuildProjectListResponse>(
                exception, _logger, "Projects.List", page, pageSize);
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<BuildProjectResponse>>> Get(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var project = await _projects.GetAsync(id, cancellationToken);
        if (project is null)
            return NotFound(new ApiResponse<BuildProjectResponse>(false, null, "Build project not found", null));
        return Ok(new ApiResponse<BuildProjectResponse>(true, ToResponse(project), null, null));
    }

    [HttpPost]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<ApiResponse<BuildProjectResponse>>> Create(
        [FromBody] CreateBuildProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var createdBy = User.Identity?.Name ?? "system";
            var project = await _projects.CreateAsync(request, createdBy, cancellationToken);
            return CreatedAtAction(
                nameof(Get),
                new { id = project.Id },
                new ApiResponse<BuildProjectResponse>(
                    true, ToResponse(project), null, "Build project created"));
        }
        catch (Exception exception)
        {
            return ApiResults.FromException<BuildProjectResponse>(
                exception, _logger, "Projects.Create", request.Name);
        }
    }

    [HttpGet("{id:guid}/pipelines")]
    public async Task<ActionResult<ApiResponse<List<BuildProjectPipelineResponse>>>> ListPipelines(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pipelines = await _projects.ListPipelineBindingsAsync(id, cancellationToken);
            return Ok(new ApiResponse<List<BuildProjectPipelineResponse>>(
                true,
                pipelines.Select(ToPipelineResponse).ToList(),
                null,
                null));
        }
        catch (Exception exception)
        {
            return ApiResults.FromException<List<BuildProjectPipelineResponse>>(
                exception, _logger, "Projects.ListPipelines", id);
        }
    }

    [HttpPut("{id:guid}/pipelines")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<ApiResponse<BuildProjectPipelineResponse>>> BindPipeline(
        Guid id,
        [FromBody] BindProjectPipelineRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pipeline = await _projects.BindPipelineAsync(id, request, cancellationToken);
            return Ok(new ApiResponse<BuildProjectPipelineResponse>(
                true, ToPipelineResponse(pipeline), null, "Pipeline bound to project package"));
        }
        catch (Exception exception)
        {
            return ApiResults.FromException<BuildProjectPipelineResponse>(
                exception, _logger, "Projects.BindPipeline", id, request.PipelineId);
        }
    }

    [HttpDelete("{id:guid}/pipelines/{packageId}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<ApiResponse<object>>> UnbindPipeline(
        Guid id,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _projects.UnbindPipelineAsync(id, packageId, cancellationToken))
            {
                return NotFound(new ApiResponse<object>(
                    false, null, "Project package binding not found", null));
            }
            return Ok(new ApiResponse<object>(true, null, null, "Pipeline unbound"));
        }
        catch (Exception exception)
        {
            return ApiResults.FromException<object>(
                exception, _logger, "Projects.UnbindPipeline", id, packageId);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<ApiResponse<BuildProjectResponse>>> Update(
        Guid id,
        [FromBody] UpdateBuildProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var project = await _projects.UpdateAsync(id, request, cancellationToken);
            return Ok(new ApiResponse<BuildProjectResponse>(
                true, ToResponse(project), null, "Build project updated"));
        }
        catch (Exception exception)
        {
            return ApiResults.FromException<BuildProjectResponse>(
                exception, _logger, "Projects.Update", id);
        }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _projects.DeleteAsync(id, cancellationToken))
                return NotFound(new ApiResponse<object>(false, null, "Build project not found", null));
            return Ok(new ApiResponse<object>(true, null, null, "Build project deleted"));
        }
        catch (Exception exception)
        {
            return ApiResults.FromException<object>(exception, _logger, "Projects.Delete", id);
        }
    }

    private BuildProjectResponse ToResponse(BuildProject project) => new(
        project.Id,
        project.Name,
        project.GitRepoUrl,
        project.GitBranch,
        project.ManifestPath,
        project.IsActive,
        project.CreatedBy,
        project.CreatedAt,
        project.UpdatedAt,
        $"{Request.Scheme}://{Request.Host}/api/webhooks/projects/{project.Id}",
        !string.IsNullOrEmpty(project.WebhookSecret),
        project.GitUsername,
        !string.IsNullOrEmpty(project.GitToken));

    private static BuildProjectPipelineResponse ToPipelineResponse(Pipeline pipeline) => new(
        pipeline.BuildProjectId!.Value,
        pipeline.PackageId!,
        pipeline.Id,
        pipeline.Name,
        pipeline.Status);
}
