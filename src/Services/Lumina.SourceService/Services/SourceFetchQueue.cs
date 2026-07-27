using Lumina.SourceService.Data;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;

namespace Lumina.SourceService.Services;

/// <summary>Persists source-fetch requests as immutable Pending attempts.</summary>
public sealed class SourceFetchQueue
{
    private readonly SourceDbContext _db;
    private readonly ILogger<SourceFetchQueue> _logger;
    private readonly int _defaultMaxRetries;

    public SourceFetchQueue(
        SourceDbContext db,
        IConfiguration configuration,
        ILogger<SourceFetchQueue> logger)
    {
        _db = db;
        _logger = logger;
        _defaultMaxRetries = Math.Clamp(
            configuration.GetValue("Source:MaxRetries", 3), 0, 10);
    }

    public async Task<SourceJob> EnqueueAsync(
        string packageName,
        string sourceUrl,
        SourceType sourceType,
        string? branch = null,
        int? maxRetries = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveRetries = Math.Clamp(
            maxRetries ?? _defaultMaxRetries, 0, 10);
        var now = DateTime.UtcNow;
        var job = new SourceJob
        {
            Id = Guid.NewGuid(),
            PackageName = packageName,
            SourceUrl = sourceUrl,
            SourceType = sourceType,
            SourceBranch = branch,
            Status = SourceStatus.Pending,
            MaxRetries = effectiveRetries,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.SourceJobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Queued source fetch attempt {JobId} for {Package}", job.Id, packageName);
        return job;
    }

    public async Task<List<SourceJob>> EnqueueAllAsync(
        ConfigParserService configParser,
        int? maxRetries = null,
        CancellationToken cancellationToken = default)
    {
        var jobs = new List<SourceJob>();
        foreach (var package in configParser.ParsePackages())
        {
            jobs.Add(await EnqueueAsync(
                package.Name,
                package.Source,
                package.SourceType,
                package.SourceBranch,
                maxRetries,
                cancellationToken));
        }
        return jobs;
    }
}
