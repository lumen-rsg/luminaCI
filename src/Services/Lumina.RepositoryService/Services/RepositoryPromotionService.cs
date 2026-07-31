using Lumina.RepositoryService.Data;
using Lumina.Shared.Errors;
using Lumina.Shared.Events;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;
using MassTransit;
using Minio;
using Minio.DataModel.Args;

namespace Lumina.RepositoryService.Services;

public sealed class RepositoryPromotionService(
    RepositoryDbContext db,
    RepositoryManagerService repositories,
    IMinioClient minio,
    IPublishEndpoint publisher,
    ILogger<RepositoryPromotionService> logger)
{
    private const string ArtifactBucket = "lumina-artifacts";

    public async Task PromoteAsync(Guid promotionSetId, CancellationToken cancellationToken)
    {
        var staging = string.Empty;
        PromotionPublicationCommit? filesystemCommit = null;
        var databaseCommitted = false;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var setIdentity = await db.PromotionSets.AsNoTracking()
                .Where(item => item.Id == promotionSetId)
                .Select(item => new { item.RepositoryId })
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new ValidationException("Promotion set was not found.");
            var repository = await db.Repositories.FromSqlInterpolated(
                    $"""SELECT * FROM "Repositories" WHERE "Id" = {setIdentity.RepositoryId} FOR UPDATE""")
                .SingleAsync(cancellationToken);
            var set = await db.PromotionSets.Include(item => item.Packages)
                .SingleAsync(item => item.Id == promotionSetId, cancellationToken);
            if (set.Status == PromotionSetStatus.Promoted)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            if (set.Status != PromotionSetStatus.Passed || set.Packages.Count == 0 ||
                set.Packages.Any(item => item.Status != "Candidate") ||
                set.GateBaselineManifestSha256 is not { Length: 64 } baselineHash)
                throw new ConflictException("Promotion set is not ready for atomic publication.");

            var currentBaseline = await db.Packages.AsNoTracking()
                .Where(item => item.RepositoryId == repository.Id && item.Status == "Ready" &&
                               (item.Arch == set.TargetArchitecture || item.Arch == "noarch"))
                .ToListAsync(cancellationToken);
            if (!string.Equals(
                    RepositoryManifestPolicy.Compute(currentBaseline), baselineHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new ConflictException(
                    "Live repository changed after the native gate snapshot; a new gate is required.");

            staging = repositories.CreatePublicationStagingDirectory(repository.Id);
            var snapshot = repositories.CreateRepositorySnapshot(repository.BasePath, staging);
            var candidateFiles = new List<PromotionCandidateFile>(set.Packages.Count);
            foreach (var package in set.Packages.OrderBy(item => item.Arch).ThenBy(item => item.FileName))
            {
                var destination = repositories.GetRepositorySnapshotRpmPath(
                    snapshot, package.Arch, package.FileName);
                await minio.GetObjectAsync(new GetObjectArgs()
                    .WithBucket(ArtifactBucket)
                    .WithObject(package.CandidateObjectName!)
                    .WithFile(destination), cancellationToken);
                ValidateCandidate(destination, package);
                File.SetLastWriteTimeUtc(destination, DateTime.UnixEpoch);
                candidateFiles.Add(new PromotionCandidateFile(package.Arch, package.FileName));
            }
            await repositories.GenerateRepositorySnapshotMetadataAsync(
                snapshot, candidateFiles.Select(item => item.Architecture));
            repositories.WritePromotionJournal(
                staging, set.Id, repository.Id, repository.BasePath, candidateFiles);
            filesystemCommit = repositories.CommitStagedRepository(
                repository.BasePath, snapshot, candidateFiles);

            var now = DateTime.UtcNow;
            foreach (var package in set.Packages)
            {
                package.Status = "Ready";
                package.StoragePath = $"{repository.BasePath}/{package.Arch}/{package.FileName}";
                package.PublishedAt = now;
            }
            var existingReady = await db.Packages.AsNoTracking()
                .Where(item => item.RepositoryId == repository.Id && item.Status == "Ready" &&
                               item.PromotionSetId != set.Id)
                .ToListAsync(cancellationToken);
            var repositoryHash = RepositoryManifestPolicy.Compute(existingReady.Concat(set.Packages));
            RepositoryPromotionPolicy.MarkPromoted(
                set, repositoryHash, filesystemCommit.PreviousSnapshotPath, now);
            repository.UpdatedAt = now;
            await publisher.Publish(new PromotionSetPublished(
                set.Id, repository.Id,
                set.Packages.Select(item => item.ArtifactId!.Value).Order().ToList(), now),
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            databaseCommitted = true;
            logger.LogInformation(
                "Atomically promoted set {PromotionSetId} with {PackageCount} packages; rollback snapshot {Snapshot}",
                set.Id, set.Packages.Count, filesystemCommit.PreviousSnapshotPath);
        }
        catch
        {
            if (!databaseCommitted)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                if (filesystemCommit is not null)
                    repositories.RollbackStagedRepository(filesystemCommit);
                if (!string.IsNullOrWhiteSpace(staging))
                    repositories.CleanupPublicationStaging(staging);
            }
            throw;
        }
    }

    public async Task RollbackAsync(
        Guid repositoryId,
        Guid promotionSetId,
        string rolledBackBy,
        string reason,
        CancellationToken cancellationToken)
    {
        PromotionPublicationCommit? filesystemCommit = null;
        string? staging = null;
        var databaseCommitted = false;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            _ = await db.Repositories.FromSqlInterpolated(
                    $"""SELECT * FROM "Repositories" WHERE "Id" = {repositoryId} FOR UPDATE""")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new ValidationException("Rollback repository was not found.");
            var set = await db.PromotionSets.Include(item => item.Packages)
                .SingleOrDefaultAsync(item => item.Id == promotionSetId && item.RepositoryId == repositoryId,
                    cancellationToken)
                ?? throw new ValidationException("Rollback promotion set was not found.");
            if (set.Status == PromotionSetStatus.RolledBack)
            {
                RepositoryPromotionPolicy.MarkRolledBack(
                    set, rolledBackBy, reason, DateTime.UtcNow);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            if (set.Status != PromotionSetStatus.Promoted ||
                set.PromotedRepositoryManifestSha256 is not { Length: 64 } expectedManifest ||
                string.IsNullOrWhiteSpace(set.RollbackSnapshotPath))
                throw new ConflictException("Promotion set has no recoverable rollback snapshot.");
            if (await db.PromotionSets.AnyAsync(item => item.RepositoryId == repositoryId &&
                    item.Status == PromotionSetStatus.Promoted && item.PromotedAt > set.PromotedAt,
                    cancellationToken))
                throw new ConflictException("A newer promotion must be rolled back first.");
            var currentReady = await db.Packages.AsNoTracking()
                .Where(item => item.RepositoryId == repositoryId && item.Status == "Ready")
                .ToListAsync(cancellationToken);
            if (!string.Equals(RepositoryManifestPolicy.Compute(currentReady), expectedManifest,
                    StringComparison.OrdinalIgnoreCase))
                throw new ConflictException(
                    "Live repository changed after promotion; rollback would discard newer packages.");

            var journal = repositories.ReadPromotionJournals()
                .Select(item => item.Journal)
                .SingleOrDefault(item => item.PromotionSetId == set.Id && item.RepositoryId == repositoryId)
                ?? throw new ConflictException("Promotion rollback journal is unavailable.");
            filesystemCommit = repositories.PublicationFromJournal(journal);
            if (filesystemCommit.PreviousSnapshotPath != set.RollbackSnapshotPath ||
                !repositories.LiveContainsPromotionCandidates(journal))
                throw new ConflictException("Promotion rollback snapshot identity changed.");
            staging = journal.StagingDirectory;
            repositories.RollbackStagedRepository(filesystemCommit);
            foreach (var package in set.Packages)
            {
                package.Status = "RolledBack";
                package.StoragePath = package.CandidateObjectName!;
            }
            RepositoryPromotionPolicy.MarkRolledBack(
                set, rolledBackBy, reason, DateTime.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            databaseCommitted = true;
            try
            {
                repositories.CompleteStagedRepository(filesystemCommit);
                repositories.CleanupPublicationStaging(staging);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "Rollback {PromotionSetId} committed but snapshot cleanup requires startup recovery",
                    set.Id);
            }
            logger.LogWarning(
                "Rolled back promotion set {PromotionSetId} by {Actor}: {Reason}",
                set.Id, rolledBackBy, reason);
        }
        catch
        {
            if (!databaseCommitted)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                if (filesystemCommit is not null)
                    repositories.RollbackStagedRepository(filesystemCommit);
            }
            throw;
        }
    }

    public async Task RecoverInterruptedPromotionsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (_, journal) in repositories.ReadPromotionJournals())
        {
            var set = await db.PromotionSets.Include(item => item.Packages)
                .SingleOrDefaultAsync(item => item.Id == journal.PromotionSetId &&
                                              item.RepositoryId == journal.RepositoryId,
                    cancellationToken);
            var publication = repositories.PublicationFromJournal(journal);
            var liveHasCandidates = repositories.LiveContainsPromotionCandidates(journal);
            var shouldBePromoted = set?.Status == PromotionSetStatus.Promoted;
            if (liveHasCandidates != shouldBePromoted)
                repositories.RollbackStagedRepository(publication);
            if (shouldBePromoted)
            {
                if (set!.RollbackSnapshotPath != publication.PreviousSnapshotPath ||
                    set.Packages.Any(item => item.Status != "Ready"))
                    throw new InvalidOperationException("Committed promotion recovery identity is invalid.");
                logger.LogWarning(
                    "Recovered committed promotion {PromotionSetId}; rollback snapshot retained",
                    set.Id);
                continue;
            }
            repositories.CompleteStagedRepository(publication);
            repositories.CleanupPublicationStaging(journal.StagingDirectory);
            logger.LogWarning(
                "Recovered uncommitted or rolled-back promotion {PromotionSetId}", journal.PromotionSetId);
        }
    }

    private void ValidateCandidate(string path, Package package)
    {
        var info = new FileInfo(path);
        var hash = repositories.ComputeSha256(path);
        var metadata = repositories.ValidateStagedRpm(path, package.FileName);
        if (info.Length != package.FileSize ||
            !string.Equals(hash, package.HashSha256, StringComparison.OrdinalIgnoreCase) ||
            metadata.Name != package.Name || metadata.Version != package.Version ||
            metadata.Release != package.Release || metadata.Arch != package.Arch)
            throw new ValidationException($"Promotion candidate '{package.FileName}' failed exact validation.");
    }
}
