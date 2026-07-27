using Lumina.Shared.DTOs;
using Lumina.Shared.Models;
using Lumina.SourceService.Data;
using Microsoft.EntityFrameworkCore;

namespace Lumina.SourceService.Services;

public sealed class PackageConflictException : Exception
{
    public PackageConflictException(string message) : base(message) { }
}

/// <summary>
/// Database-backed source catalog. Definitions have stable identities while
/// every edit appends an immutable revision and optionally queues that exact
/// revision for resolution.
/// </summary>
public sealed class PackageCatalogService
{
    private readonly SourceDbContext _db;
    private readonly SourceFetchQueue _fetchQueue;

    public PackageCatalogService(SourceDbContext db, SourceFetchQueue fetchQueue)
    {
        _db = db;
        _fetchQueue = fetchQueue;
    }

    public Task<List<PackageDefinition>> ListAsync(CancellationToken cancellationToken = default)
        => _db.PackageDefinitions
            .AsNoTracking()
            .Where(package => package.IsEnabled)
            .Include(package => package.Revisions)
            .OrderBy(package => package.Slug)
            .ToListAsync(cancellationToken);

    public Task<PackageDefinition?> GetAsync(
        string slug,
        bool tracking = false,
        CancellationToken cancellationToken = default)
    {
        var normalized = PackageSourcePolicy.NormalizeSlug(slug);
        var query = _db.PackageDefinitions
            .Include(package => package.Revisions)
            .Where(package => package.Slug == normalized);
        return (tracking ? query : query.AsNoTracking()).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<(PackageDefinition Package, SourceJob? Job)> CreateAsync(
        SavePackageSourceRequest request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        PackageSourcePolicy.Validate(request);
        var slug = PackageSourcePolicy.NormalizeSlug(request.Slug);
        if (await _db.PackageDefinitions.AnyAsync(package => package.Slug == slug, cancellationToken))
            throw new PackageConflictException($"Package '{slug}' already exists.");

        var now = DateTime.UtcNow;
        var package = new PackageDefinition
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            IsEnabled = request.IsEnabled,
            ActiveRevisionNumber = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        var revision = CreateRevision(package, request, 1, actor, now);
        package.Revisions.Add(revision);
        _db.PackageDefinitions.Add(package);

        SourceJob? job = null;
        if (request.IsEnabled && request.FetchAutomatically)
        {
            job = _fetchQueue.CreatePending(revision, slug);
            _db.SourceJobs.Add(job);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return (package, job);
    }

    public async Task<(PackageDefinition Package, SourceJob? Job)> UpdateAsync(
        string slug,
        SavePackageSourceRequest request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        PackageSourcePolicy.Validate(request);
        var normalized = PackageSourcePolicy.NormalizeSlug(slug);
        if (PackageSourcePolicy.NormalizeSlug(request.Slug) != normalized)
            throw new SourceValidationException("A package slug cannot be changed.");

        var package = await _db.PackageDefinitions
            .Include(item => item.Revisions)
            .SingleOrDefaultAsync(item => item.Slug == normalized, cancellationToken)
            ?? throw new KeyNotFoundException($"Package '{normalized}' was not found.");

        if (request.ExpectedRevision is null)
            throw new SourceValidationException(
                "ExpectedRevision is required when updating a package.");
        if (request.ExpectedRevision != package.ActiveRevisionNumber)
            throw new PackageConflictException(
                $"Package '{normalized}' is at revision {package.ActiveRevisionNumber}, not {request.ExpectedRevision}.");

        var now = DateTime.UtcNow;
        var revisionNumber = package.ActiveRevisionNumber + 1;
        var revision = CreateRevision(package, request, revisionNumber, actor, now);
        package.Revisions.Add(revision);
        _db.PackageRevisions.Add(revision);
        package.ActiveRevisionNumber = revisionNumber;
        package.IsEnabled = request.IsEnabled;
        package.UpdatedAt = now;

        SourceJob? job = null;
        if (request.IsEnabled && request.FetchAutomatically)
        {
            job = _fetchQueue.CreatePending(revision, normalized);
            _db.SourceJobs.Add(job);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return (package, job);
    }

    public async Task DisableAsync(
        string slug,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var normalized = PackageSourcePolicy.NormalizeSlug(slug);
        var package = await _db.PackageDefinitions
            .SingleOrDefaultAsync(item => item.Slug == normalized, cancellationToken)
            ?? throw new KeyNotFoundException($"Package '{normalized}' was not found.");
        if (package.ActiveRevisionNumber != expectedRevision)
            throw new PackageConflictException(
                $"Package '{normalized}' is at revision {package.ActiveRevisionNumber}, not {expectedRevision}.");

        package.IsEnabled = false;
        package.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static PackageRevision CreateRevision(
        PackageDefinition package,
        SavePackageSourceRequest request,
        int revisionNumber,
        string actor,
        DateTime now)
        => new()
        {
            Id = Guid.NewGuid(),
            PackageDefinitionId = package.Id,
            RevisionNumber = revisionNumber,
            SourceType = request.SourceType,
            SourceUrl = request.SourceUrl,
            SourceReference = request.SourceReference,
            ExpectedSha256 = request.ExpectedSha256?.ToLowerInvariant(),
            SpecPath = request.SpecPath,
            BuildImage = request.BuildImage,
            CreatedAt = now,
            CreatedBy = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor
        };
}
