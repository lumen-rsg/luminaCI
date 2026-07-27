using Lumina.SourceService.Data;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.SourceService.Services;

/// <summary>
/// Database-backed, bounded source-fetch worker. Pending jobs and expired
/// leases survive process restarts; PostgreSQL SKIP LOCKED coordinates replicas.
/// </summary>
public sealed class SourceFetchWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SourceFetchWorker> _logger;
    private readonly int _maxConcurrency;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _leaseDuration;
    private readonly string _workerId =
        $"{Environment.MachineName}-{Guid.NewGuid():N}";
    private readonly Dictionary<Guid, RunningJob> _running = [];

    public SourceFetchWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<SourceFetchWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _maxConcurrency = Math.Clamp(
            configuration.GetValue("Source:WorkerConcurrency", 2), 1, 16);
        _leaseDuration = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("Source:LeaseSeconds", 60), 30, 600));
        var configuredPollSeconds = Math.Clamp(
            configuration.GetValue("Source:WorkerPollSeconds", 2), 1, 30);
        _pollInterval = TimeSpan.FromSeconds(Math.Min(
            configuredPollSeconds, Math.Max(1, (int)_leaseDuration.TotalSeconds / 3)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Source worker {WorkerId} started with concurrency {Concurrency}",
            _workerId, _maxConcurrency);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ObserveCompletedJobsAsync();
                await HeartbeatRunningJobsAsync(stoppingToken);

                while (_running.Count < _maxConcurrency && !stoppingToken.IsCancellationRequested)
                {
                    var jobId = await ClaimNextJobAsync(stoppingToken);
                    if (jobId is null)
                        break;

                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var task = ExecuteClaimedJobAsync(jobId.Value, cancellation.Token);
                    _running.Add(jobId.Value, new RunningJob(task, cancellation));
                }

                await Task.Delay(_pollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            foreach (var running in _running.Values)
                running.Cancellation.Cancel();
            try { await Task.WhenAll(_running.Values.Select(r => r.Task)); }
            catch (Exception ex) { _logger.LogDebug(ex, "A source job stopped during host shutdown"); }
            foreach (var running in _running.Values)
                running.Cancellation.Dispose();
            _running.Clear();
        }
    }

    private async Task<Guid?> ClaimNextJobAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SourceDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;

        var claimed = await db.SourceJobs
            .FromSqlInterpolated(
                $"""
                 SELECT * FROM source.source_jobs
                 WHERE "Status" = {(int)SourceStatus.Pending}
                    OR ("Status" = {(int)SourceStatus.Fetching}
                        AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" < {now}))
                 ORDER BY "CreatedAt"
                 FOR UPDATE SKIP LOCKED
                 LIMIT 1
                 """)
            .ToListAsync(cancellationToken);
        var job = claimed.SingleOrDefault();

        if (job is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (job.CancellationRequested)
        {
            job.Status = SourceStatus.Cancelled;
            job.FetchCompletedAt = now;
            job.UpdatedAt = now;
            job.ErrorMessage = "Cancelled by request.";
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            job.HeartbeatAt = null;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        job.Status = SourceStatus.Fetching;
        job.FetchStartedAt ??= now;
        job.UpdatedAt = now;
        job.LeaseOwner = _workerId;
        job.HeartbeatAt = now;
        job.LeaseExpiresAt = now + _leaseDuration;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("Worker {WorkerId} claimed source job {JobId}", _workerId, job.Id);
        return job.Id;
    }

    private async Task ExecuteClaimedJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<SourceFetchService>();
            await service.ExecuteFetchAsync(jobId, _workerId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled failure while executing source job {JobId}", jobId);
        }
    }

    private async Task HeartbeatRunningJobsAsync(CancellationToken cancellationToken)
    {
        foreach (var (jobId, running) in _running)
        {
            if (running.Task.IsCompleted)
                continue;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SourceDbContext>();
            var job = await db.SourceJobs.SingleOrDefaultAsync(
                j => j.Id == jobId &&
                     j.Status == SourceStatus.Fetching &&
                     j.LeaseOwner == _workerId,
                cancellationToken);
            if (job is null)
            {
                running.Cancellation.Cancel();
                continue;
            }

            if (job.CancellationRequested)
            {
                running.Cancellation.Cancel();
                continue;
            }

            var now = DateTime.UtcNow;
            job.HeartbeatAt = now;
            job.LeaseExpiresAt = now + _leaseDuration;
            job.UpdatedAt = now;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                running.Cancellation.Cancel();
                _logger.LogWarning(
                    "Worker {WorkerId} lost the heartbeat lease for source job {JobId}",
                    _workerId, jobId);
            }
        }
    }

    private async Task ObserveCompletedJobsAsync()
    {
        foreach (var jobId in _running
                     .Where(pair => pair.Value.Task.IsCompleted)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            var running = _running[jobId];
            try { await running.Task; }
            catch (Exception ex) { _logger.LogError(ex, "Source job {JobId} task faulted", jobId); }
            running.Cancellation.Dispose();
            _running.Remove(jobId);
        }
    }

    private sealed record RunningJob(Task Task, CancellationTokenSource Cancellation);
}
