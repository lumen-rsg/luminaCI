using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;

namespace Lumina.BuildService.Services;

/// <summary>
/// Abstraction over "start a containerized build for this job", implemented by
/// <see cref="DockerBuildService"/>. Exists as a test seam so
/// <see cref="PipelineEngine"/> can be unit-tested without a Docker daemon:
/// the engine's own logic (fail-closed gates, source-URL synthesis, job
/// persistence) is independent of how the build container is actually launched.
/// </summary>
public interface IBuildLauncher
{
    BuildExecutorBackend Backend { get; }

    /// <inheritdoc cref="DockerBuildService.StartBuildAsync"/>
    Task<BuildJob> StartBuildAsync(
        BuildJob job,
        string? specContent,
        string? sourceUrl,
        string? buildImage = null,
        string? gitUsername = null,
        string? gitToken = null,
        string? extraSourcesPipelineDir = null);
}
