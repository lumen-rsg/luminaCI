namespace Lumina.BuildService.Services;

/// <summary>
/// Process-wide ownership and concurrency state for build containers. Recovered
/// containers are always monitored, even when they exceed a newly lowered limit;
/// new work waits until the total active count falls below the configured cap.
/// </summary>
public sealed class BuildExecutionCoordinator
{
    private readonly object _lock = new();
    private readonly HashSet<Guid> _active = [];
    private TaskCompletionSource _changed = NewSignal();

    public BuildExecutionCoordinator(IConfiguration configuration)
    {
        WorkerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        MaxConcurrentBuilds = Math.Max(1, configuration.GetValue("BuildMonitoring:MaxConcurrentBuilds", 4));
        LeaseDuration = TimeSpan.FromSeconds(
            Math.Max(15, configuration.GetValue("BuildMonitoring:LeaseSeconds", 60)));
        HeartbeatInterval = TimeSpan.FromSeconds(
            Math.Max(5, configuration.GetValue("BuildMonitoring:HeartbeatSeconds", 15)));
        MaxBuildDuration = TimeSpan.FromMinutes(
            Math.Max(1, configuration.GetValue("BuildMonitoring:MaxBuildMinutes", 120)));
    }

    public string WorkerId { get; }
    public int MaxConcurrentBuilds { get; }
    public TimeSpan LeaseDuration { get; }
    public TimeSpan HeartbeatInterval { get; }
    public TimeSpan MaxBuildDuration { get; }

    public async Task AcquireAsync(Guid jobId, bool recovered, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_active.Contains(jobId))
                    return;

                if (recovered || _active.Count < MaxConcurrentBuilds)
                {
                    _active.Add(jobId);
                    return;
                }

                wait = _changed.Task;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }

    public void Release(Guid jobId)
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            if (!_active.Remove(jobId))
                return;

            signal = _changed;
            _changed = NewSignal();
        }

        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
