using Lumina.SourceService.Data;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

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
        string? expectedSha256 = null,
        int? maxRetries = null,
        CancellationToken cancellationToken = default)
    {
        if (expectedSha256 is not null)
        {
            if (sourceType is not (SourceType.Tar or SourceType.Http) ||
                expectedSha256.Length != 64 ||
                expectedSha256.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new ArgumentException(
                    "Expected SHA-256 is only valid for archive sources and must contain 64 hexadecimal characters.",
                    nameof(expectedSha256));
            }
            expectedSha256 = expectedSha256.ToLowerInvariant();
        }

        var job = CreatePending(
            packageName, sourceUrl, sourceType, branch, expectedSha256, maxRetries);
        _db.SourceJobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Queued source fetch attempt {JobId} for {Package}", job.Id, packageName);
        return job;
    }

    public SourceJob CreatePending(
        PackageRevision revision,
        string packageName,
        int? maxRetries = null)
    {
        var job = CreatePending(
            packageName,
            revision.SourceUrl,
            revision.SourceType,
            revision.SourceReference,
            revision.ExpectedSha256,
            maxRetries);
        job.PackageRevisionId = revision.Id;
        job.PackageRevision = revision;
        return job;
    }

    public async Task<List<SourceJob>> EnqueueAllAsync(
        int? maxRetries = null,
        CancellationToken cancellationToken = default)
    {
        var packages = await _db.PackageDefinitions
            .Where(package => package.IsEnabled)
            .Include(package => package.Revisions)
            .ToListAsync(cancellationToken);
        var jobs = packages.Select(package =>
            CreatePending(
                package.Revisions.Single(revision =>
                    revision.RevisionNumber == package.ActiveRevisionNumber),
                package.Slug,
                maxRetries)).ToList();
        _db.SourceJobs.AddRange(jobs);
        await _db.SaveChangesAsync(cancellationToken);
        return jobs;
    }

    private SourceJob CreatePending(
        string packageName,
        string sourceUrl,
        SourceType sourceType,
        string? branch,
        string? expectedSha256,
        int? maxRetries)
    {
        if (expectedSha256 is not null)
        {
            if (sourceType is not (SourceType.Tar or SourceType.Http) ||
                expectedSha256.Length != 64 ||
                expectedSha256.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new ArgumentException(
                    "Expected SHA-256 is only valid for archive sources and must contain 64 hexadecimal characters.",
                    nameof(expectedSha256));
            }
            expectedSha256 = expectedSha256.ToLowerInvariant();
        }

        var now = DateTime.UtcNow;
        return new SourceJob
        {
            Id = Guid.NewGuid(),
            PackageName = packageName,
            SourceUrl = sourceUrl,
            SourceType = sourceType,
            SourceBranch = branch,
            ExpectedSha256 = expectedSha256,
            Status = SourceStatus.Pending,
            MaxRetries = Math.Clamp(maxRetries ?? _defaultMaxRetries, 0, 10),
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
