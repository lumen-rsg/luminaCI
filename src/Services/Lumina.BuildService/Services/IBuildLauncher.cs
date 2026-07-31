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

/// <summary>
/// Complete lifecycle surface for a durable execution backend. New builds use
/// <see cref="IBuildLauncher"/> while recovery and cancellation resolve the
/// executor recorded immutably on each build job.
/// </summary>
public interface IBuildExecutor : IBuildLauncher
{
    Task MonitorBuildAsync(
        BuildJob job,
        CancellationToken cancellationToken = default);

    Task<bool> CancelBuildAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);
}

public sealed class BuildExecutorIdentityException(string message) : InvalidOperationException(message);

public sealed record BuildExecutorRegistration(
    BuildExecutorBackend Backend,
    Func<IServiceProvider, IBuildExecutor> Resolve);

public interface IBuildExecutorResolver
{
    IBuildExecutor Resolve(BuildExecutorBackend backend);
}

public sealed class BuildExecutorResolver : IBuildExecutorResolver
{
    private readonly IServiceProvider _services;
    private readonly IReadOnlyDictionary<BuildExecutorBackend, BuildExecutorRegistration> _registrations;

    public BuildExecutorResolver(
        IServiceProvider services,
        IEnumerable<BuildExecutorRegistration> registrations)
    {
        _services = services;
        var items = registrations.ToList();
        var duplicate = items
            .GroupBy(item => item.Backend)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"Executor backend '{duplicate.Key}' is registered more than once.");
        _registrations = items.ToDictionary(item => item.Backend);
    }

    public IBuildExecutor Resolve(BuildExecutorBackend backend)
    {
        if (!_registrations.TryGetValue(backend, out var registration))
            throw new InvalidOperationException($"Executor backend '{backend}' is not registered.");
        var executor = registration.Resolve(_services)
            ?? throw new InvalidOperationException($"Executor backend '{backend}' resolved to null.");
        if (executor.Backend != backend)
        {
            throw new InvalidOperationException(
                $"Executor registration '{backend}' resolved backend '{executor.Backend}'.");
        }
        return executor;
    }
}
