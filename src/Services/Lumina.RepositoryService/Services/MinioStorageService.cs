using Lumina.RepositoryService.Data;
using Lumina.Shared.Errors;
using Lumina.Shared.Events;
using Lumina.Shared.Models;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;

namespace Lumina.RepositoryService.Services;

public class MinioStorageService
{
    private readonly RepositoryDbContext _db;
    private readonly IMinioClient _minio;
    private readonly RepositoryManagerService _repoManager;
    private readonly SignatureVerificationService _verification;
    private readonly ILogger<MinioStorageService> _logger;
    private readonly IBus _bus;
    private readonly HttpClient _artifactHttpClient;

    public MinioStorageService(
        RepositoryDbContext db,
        IMinioClient minio,
        RepositoryManagerService repoManager,
        SignatureVerificationService verification,
        ILogger<MinioStorageService> logger,
        IBus bus,
        IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _minio = minio;
        _repoManager = repoManager;
        _verification = verification;
        _logger = logger;
        _bus = bus;
        _artifactHttpClient = httpClientFactory.CreateClient("ArtifactStorage");
    }

    public async Task<PackageRepository> CreateRepositoryAsync(string name, string displayName, string basePath, string arch, string distribution, string createdBy)
    {
        // Fail fast on a duplicate Name before any MinIO bucket or filesystem
        // directory is created. The Name column has a unique index
        // (RepositoryDbContext), so relying on the DB to reject duplicates would
        // leak a driver-specific "duplicate key violates ..." message back to the
        // caller; the pre-check lets us raise a clean ConflictException instead.
        if (await _db.Repositories.AnyAsync(r => r.Name == name))
        {
            throw new ConflictException($"A repository named '{name}' already exists.");
        }

        var repo = new PackageRepository
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = displayName,
            BasePath = basePath,
            Arch = arch,
            Distribution = distribution,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedBy = createdBy
        };

        // Ensure MinIO bucket exists
        var bucketName = $"repo-{name.ToLowerInvariant()}";
        var bucketExists = await _minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucketName));
        if (!bucketExists)
        {
            await _minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucketName));
            _logger.LogInformation("Created MinIO bucket {Bucket}", bucketName);
        }

        // Ensure filesystem directory exists
        _repoManager.EnsureRepoDir(basePath, arch);

        _db.Repositories.Add(repo);
        await _db.SaveChangesAsync();
        return repo;
    }

    /// <summary>
    /// Fetches metadata recorded only after an embedded RPM signature was
    /// verified. A missing fingerprint or final signed digest blocks publication.
    /// </summary>
    private async Task<ArtifactSigningMetadata> GetRequiredArtifactSigningAsync(Guid artifactId)
    {
        ArtifactSigningMetadata signing;
        try
        {
            var response = await _bus.Request<GetArtifactSigningMetadata, ArtifactSigningMetadata>(
                new GetArtifactSigningMetadata(artifactId), timeout: TimeSpan.FromSeconds(10));
            signing = response.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch RPM signing metadata for artifact {ArtifactId}", artifactId);
            throw new ValidationException(
                $"Could not confirm an embedded RPM signature for artifact {artifactId}. Unsigned packages cannot be published.");
        }

        if (string.IsNullOrWhiteSpace(signing.KeyFingerprint) ||
            string.IsNullOrWhiteSpace(signing.SignedSha256) ||
            signing.SignedAt is null)
        {
            throw new ValidationException(
                $"Artifact {artifactId} has no verified embedded RPM signature.");
        }

        return signing;
    }

    /// <summary>
    /// Publishes a package by requesting only its immutable object reference
    /// over the message bus, then streaming the RPM from MinIO.
    /// </summary>
    public async Task<Package> PublishPackageAsync(
        Guid artifactId,
        Guid repositoryId,
        string publishedBy)
    {
        var signing = await GetRequiredArtifactSigningAsync(artifactId);
        return await PublishPackageAsync(
            artifactId,
            repositoryId,
            signing.SignedSha256!,
            publishedBy);
    }

    public async Task<Package> PublishPackageAsync(
        Guid artifactId,
        Guid repositoryId,
        string expectedSha256,
        string publishedBy)
    {
        var repo = await _db.Repositories.FindAsync(repositoryId)
            ?? throw new NotFoundException($"Repository {repositoryId} not found");

        ArtifactLocation artifact;
        try
        {
            var response = await _bus.Request<GetArtifactLocation, ArtifactLocation>(
                new GetArtifactLocation(artifactId, expectedSha256), timeout: TimeSpan.FromSeconds(10));
            artifact = response.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch artifact {ArtifactId} object location", artifactId);
            throw new ValidationException(
                $"Could not resolve artifact {artifactId}. The signed object may not be available.");
        }

        var signing = await GetRequiredArtifactSigningAsync(artifactId);
        if (!string.Equals(signing.SignedSha256, artifact.HashSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException(
                $"Artifact {artifactId} signing metadata does not match its final signed digest.");
        }

        // FUNC-007: preserve the real NEVRA filename instead of fabricating
        // package-<guid>.rpm. Basename defensively — the FileName comes from
        // BuildArtifact.FileName and should already be a bare name, but treat
        // any caller-supplied value as untrusted before filesystem use.
        var fileName = Path.GetFileName(artifact.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ValidationException($"Artifact {artifactId} has no usable filename.");

        var stagingDirectory = _repoManager.CreatePublicationStagingDirectory(repositoryId);
        try
        {
            var expectedObjectName = $"sha256/{artifact.HashSha256.ToLowerInvariant()}/{fileName}";
            if (!string.Equals(artifact.BucketName, "lumina-artifacts", StringComparison.Ordinal) ||
                !string.Equals(artifact.ObjectName, expectedObjectName, StringComparison.Ordinal))
            {
                throw new ValidationException(
                    $"Artifact {artifactId} has an invalid content-addressed object reference.");
            }

            var stagedPath = _repoManager.GetStagedRpmPath(stagingDirectory, fileName);
            try
            {
                var downloadUrl = await _minio.PresignedGetObjectAsync(
                    new PresignedGetObjectArgs()
                        .WithBucket(artifact.BucketName)
                        .WithObject(artifact.ObjectName)
                        .WithExpiry(120));
                using var response = await _artifactHttpClient.GetAsync(
                    downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var stagedFile = new FileStream(
                    stagedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await source.CopyToAsync(stagedFile);
                await stagedFile.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stream artifact {ArtifactId} from {ObjectName}", artifactId, artifact.ObjectName);
                throw new ValidationException($"Could not download immutable object for artifact {artifactId}.");
            }

            var stagedHash = _repoManager.ComputeSha256(stagedPath);
            if (!string.Equals(stagedHash, artifact.HashSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(stagedHash, signing.SignedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ValidationException(
                    $"Artifact {artifactId} bytes do not match the verified signed SHA-256 digest " +
                    $"(expected {signing.SignedSha256}, got {stagedHash}).");
            }
            if (new FileInfo(stagedPath).Length != artifact.FileSize)
                throw new ValidationException($"Artifact {artifactId} size does not match its build record.");

            var metadata = _repoManager.ValidateStagedRpm(stagedPath, fileName);
            await _verification.VerifyEmbeddedRpmAsync(stagedPath, signing.KeyFingerprint!);

            var package = new Package
            {
                Id = Guid.NewGuid(),
                RepositoryId = repositoryId,
                ArtifactId = artifactId,
                Name = metadata.Name,
                Version = metadata.Version,
                Release = metadata.Release,
                Arch = metadata.Arch,
                FileName = fileName,
                StoragePath = $"{repo.BasePath}/{metadata.Arch}/{fileName}",
                FileSize = new FileInfo(stagedPath).Length,
                HashSha256 = stagedHash,
                SigningKeyFingerprint = signing.KeyFingerprint,
                PublishedAt = DateTime.UtcNow,
                PublishedBy = publishedBy,
                Status = "Staging"
            };
            return await CommitPublicationAsync(repo, package, stagingDirectory, stagedPath);
        }
        finally
        {
            TryCleanupStaging(stagingDirectory);
        }
    }

    /// <summary>
    /// Uploads an RPM file directly to the repository and creates a database record.
    /// The caller MUST have already verified <paramref name="pgpSignature"/> against
    /// the uploaded RPM (see <see cref="SignatureVerificationService"/>); an empty
    /// signature is rejected here as a defense-in-depth check.
    /// </summary>
    public async Task<Package> UploadAndPublishPackageAsync(Guid repositoryId, string fileName, Stream fileStream, long fileSize, string publishedBy, string? pgpSignature)
    {
        // Defense-in-depth: the controller must verify the signature before
        // calling this, but reject here too in case a future caller forgets.
        if (string.IsNullOrWhiteSpace(pgpSignature))
        {
            throw new ValidationException(
                "Uploaded RPM has no verified PGP signature. Provide a detached .asc signature that verifies against the active public key.");
        }

        var repo = await _db.Repositories.FindAsync(repositoryId)
            ?? throw new NotFoundException($"Repository {repositoryId} not found");

        var safeFileName = Path.GetFileName(fileName);
        var stagingDirectory = _repoManager.CreatePublicationStagingDirectory(repositoryId);
        try
        {
            var stagedPath = await _repoManager.WriteStagedRpmAsync(stagingDirectory, safeFileName, fileStream);
            var actualSize = new FileInfo(stagedPath).Length;
            if (actualSize != fileSize)
                throw new ValidationException("Uploaded RPM size changed while it was being staged.");
            var hash = _repoManager.ComputeSha256(stagedPath);
            var metadata = _repoManager.ValidateStagedRpm(stagedPath, safeFileName);

            var package = new Package
            {
                Id = Guid.NewGuid(),
                RepositoryId = repositoryId,
                ArtifactId = null,
                Name = metadata.Name,
                Version = metadata.Version,
                Release = metadata.Release,
                Arch = metadata.Arch,
                FileName = safeFileName,
                StoragePath = $"{repo.BasePath}/{metadata.Arch}/{safeFileName}",
                FileSize = actualSize,
                HashSha256 = hash,
                PgpSignature = pgpSignature,
                PublishedAt = DateTime.UtcNow,
                PublishedBy = publishedBy,
                Status = "Staging"
            };
            return await CommitPublicationAsync(repo, package, stagingDirectory, stagedPath);
        }
        finally
        {
            TryCleanupStaging(stagingDirectory);
        }
    }

    private async Task<Package> CommitPublicationAsync(
        PackageRepository repository,
        Package package,
        string stagingDirectory,
        string stagedRpmPath)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        PublicationCommit? filesystemCommit = null;
        var databaseCommitted = false;
        try
        {
            // PostgreSQL row lock serializes publishers across service replicas.
            await _db.Repositories
                .FromSqlInterpolated(
                    $"""SELECT * FROM "Repositories" WHERE "Id" = {repository.Id} FOR UPDATE""")
                .SingleAsync();

            if (package.ArtifactId is Guid artifactId)
            {
                var existing = await _db.Packages
                    .SingleOrDefaultAsync(p =>
                        p.RepositoryId == repository.Id && p.ArtifactId == artifactId);
                if (existing is not null)
                {
                    await transaction.RollbackAsync();
                    return existing;
                }
            }

            if (await _db.Packages.AnyAsync(p =>
                    p.RepositoryId == repository.Id &&
                    (p.FileName == package.FileName ||
                     (p.Name == package.Name &&
                      p.Version == package.Version &&
                      p.Release == package.Release &&
                      p.Arch == package.Arch))))
            {
                throw new ConflictException(
                    $"Package {package.Name}-{package.Version}-{package.Release}.{package.Arch} already exists.");
            }

            _db.Packages.Add(package);
            await _db.SaveChangesAsync();

            var stagedSnapshot = await _repoManager.GenerateStagedMetadataAsync(
                repository.BasePath, package.Arch, stagingDirectory, stagedRpmPath);
            _repoManager.WritePublicationJournal(
                stagingDirectory, package.Id, repository.BasePath, package.Arch, package.FileName);
            filesystemCommit = _repoManager.CommitStagedPublication(
                repository.BasePath, package.Arch, stagedSnapshot, package.FileName);

            package.Status = "Ready";
            repository.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            databaseCommitted = true;

            try
            {
                _repoManager.CompletePublication(filesystemCommit);
                _repoManager.RemovePublicationJournal(stagingDirectory);
            }
            catch (Exception ex)
            {
                // The previous metadata is hidden and no longer live. Cleanup
                // failure is recoverable and must not roll back a committed row.
                _logger.LogWarning(ex, "Failed to remove superseded repository metadata {Path}",
                    filesystemCommit.PreviousSnapshotPath);
            }

            _logger.LogInformation(
                "Package {PackageId} became Ready in repository {RepositoryId}",
                package.Id, repository.Id);
            return package;
        }
        catch
        {
            if (!databaseCommitted)
            {
                await transaction.RollbackAsync();
                try
                {
                    if (filesystemCommit is not null)
                        _repoManager.RollbackPublication(filesystemCommit);
                    _repoManager.RemovePublicationJournal(stagingDirectory);
                }
                catch (Exception recoveryException)
                {
                    _logger.LogCritical(
                        recoveryException,
                        "Immediate publication rollback failed; preserving journal {Directory} for startup recovery",
                        stagingDirectory);
                    throw;
                }
            }
            throw;
        }
    }

    public async Task RecoverInterruptedPublicationsAsync()
    {
        foreach (var (_, journal) in _repoManager.ReadPublicationJournals())
        {
            var committed = await _db.Packages.AnyAsync(
                p => p.Id == journal.PackageId && p.Status == "Ready");
            _repoManager.RecoverPublication(journal, committed);
            _logger.LogWarning(
                "Recovered interrupted publication {PackageId}; database committed: {Committed}",
                journal.PackageId, committed);
        }
        _repoManager.CleanupOrphanedPublicationStaging();
    }

    private void TryCleanupStaging(string stagingDirectory)
    {
        try
        {
            if (_repoManager.HasPublicationJournal(stagingDirectory))
            {
                _logger.LogWarning(
                    "Preserving unresolved publication journal for startup recovery: {Directory}",
                    stagingDirectory);
                return;
            }
            _repoManager.CleanupPublicationStaging(stagingDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean publication staging directory {Directory}", stagingDirectory);
        }
    }

    /// <summary>
    /// Synchronizes repository metadata using createrepo_c --update for all architectures.
    /// </summary>
    public async Task SyncRepositoryAsync(Guid repositoryId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        var repo = await _db.Repositories
            .FromSqlInterpolated(
                $"""SELECT * FROM "Repositories" WHERE "Id" = {repositoryId} FOR UPDATE""")
            .SingleOrDefaultAsync()
            ?? throw new NotFoundException($"Repository {repositoryId} not found");

        _logger.LogInformation("Syncing repository {RepoName} (basePath: {BasePath})", repo.Name, repo.BasePath);

        var results = await _repoManager.SyncAllArchAsync(repo.BasePath);

        foreach (var result in results)
        {
            if (result.Success)
            {
                _logger.LogInformation("createrepo_c sync result: {Output}", result.Output);
            }
            else
            {
                throw new InvalidOperationException($"createrepo_c sync failed: {result.Output}");
            }
        }

        repo.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        _logger.LogInformation("Repository {RepoName} sync completed", repo.Name);
    }

    public async Task<List<PackageRepository>> ListRepositoriesAsync()
    {
        return await _db.Repositories.OrderByDescending(r => r.CreatedAt).ToListAsync();
    }

    public async Task<List<Package>> ListPackagesAsync(Guid repositoryId)
    {
        return await _db.Packages
            .Where(p => p.RepositoryId == repositoryId && p.Status == "Ready")
            .OrderByDescending(p => p.PublishedAt)
            .ToListAsync();
    }
}
