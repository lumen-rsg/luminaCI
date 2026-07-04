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
    private readonly ILogger<MinioStorageService> _logger;
    private readonly IBus _bus;

    public MinioStorageService(RepositoryDbContext db, IMinioClient minio, RepositoryManagerService repoManager, ILogger<MinioStorageService> logger, IBus bus)
    {
        _db = db;
        _minio = minio;
        _repoManager = repoManager;
        _logger = logger;
        _bus = bus;
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
    /// Fetches the stored PGP signature for an artifact from BuildService over
    /// the message bus and rejects publication if none exists. RepositoryService
    /// has no view of BuildDbContext, so it cannot read the signature directly.
    /// A null/empty signature means the artifact was never signed (no active key,
    /// signing failed, or the CVE scan skipped signing) and must not be
    /// published — this is the authoritative fail-closed gate for the
    /// artifact-to-repo path.
    /// </summary>
    private async Task<string> GetRequiredArtifactSignatureAsync(Guid artifactId)
    {
        string? signature = null;
        try
        {
            var response = await _bus.Request<GetArtifactSignature, ArtifactSignature>(
                new GetArtifactSignature(artifactId), timeout: TimeSpan.FromSeconds(10));
            signature = response.Message.PgpSignature;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch PGP signature for artifact {ArtifactId} from BuildService via bus", artifactId);
            throw new ValidationException(
                $"Could not confirm a PGP signature for artifact {artifactId} (BuildService unreachable). Unsigned packages cannot be published.");
        }

        if (string.IsNullOrWhiteSpace(signature))
        {
            throw new ValidationException(
                $"Artifact {artifactId} is not PGP-signed. Unsigned packages cannot be published — generate an active PGP key and ensure the Sign stage completed before publishing.");
        }

        return signature;
    }

    /// <summary>
    /// Publishes a package by copying the RPM file to the repository directory
    /// and updating the database with real metadata.
    /// </summary>
    public async Task<Package> PublishPackageAsync(Guid artifactId, Guid repositoryId, string publishedBy)
    {
        var repo = await _db.Repositories.FindAsync(repositoryId)
            ?? throw new NotFoundException($"Repository {repositoryId} not found");

        // Try to download artifact from MinIO
        var bucketName = $"repo-{repo.Name.ToLowerInvariant()}";
        var objectName = $"{artifactId:N}.rpm";

        byte[] rpmData;
        try
        {
            using var memoryStream = new MemoryStream();
            await _minio.GetObjectAsync(new GetObjectArgs()
                .WithBucket(bucketName)
                .WithObject(objectName)
                .WithCallbackStream(stream => stream.CopyTo(memoryStream)));
            rpmData = memoryStream.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not download artifact {ArtifactId} from MinIO, creating placeholder", artifactId);
            rpmData = [];
        }

        // Save RPM file to the repository filesystem
        var fileName = $"package-{artifactId:N}.rpm";
        string? savedPath = null;

        if (rpmData.Length > 0)
        {
            savedPath = _repoManager.SaveRpm(repo.BasePath, repo.Arch, fileName, rpmData);
        }

        // Extract metadata from RPM file if available
        string pkgName = $"package-{artifactId:N}";
        string pkgVersion = "1.0.0";
        string pkgRelease = "1";
        string pkgArch = repo.Arch;
        long pkgSize = rpmData.Length;
        string? pkgHash = null;

        if (savedPath != null && rpmData.Length > 0)
        {
            pkgHash = _repoManager.ComputeSha256(savedPath);
            pkgSize = new FileInfo(savedPath).Length;

            var metadata = _repoManager.ExtractRpmMetadata(savedPath);
            if (metadata != null)
            {
                pkgName = metadata.Name;
                pkgVersion = metadata.Version;
                pkgRelease = metadata.Release;
                pkgArch = metadata.Arch;
                if (metadata.Size > 0)
                    pkgSize = metadata.Size;

                // Use real filename if metadata extracted successfully
                fileName = Path.GetFileName(savedPath);
            }
        }

        var package = new Package
        {
            Id = Guid.NewGuid(),
            RepositoryId = repositoryId,
            ArtifactId = artifactId,
            Name = pkgName,
            Version = pkgVersion,
            Release = pkgRelease,
            Arch = pkgArch,
            FileName = fileName,
            StoragePath = $"{repo.BasePath}/{repo.Arch}/{fileName}",
            FileSize = pkgSize,
            HashSha256 = pkgHash,
            PgpSignature = await GetRequiredArtifactSignatureAsync(artifactId),
            PublishedAt = DateTime.UtcNow,
            PublishedBy = publishedBy
        };

        _db.Packages.Add(package);
        await _db.SaveChangesAsync();

        // Run createrepo_c --update to update repository metadata
        if (savedPath != null)
        {
            try
            {
                var result = await _repoManager.RunCreaterepoAsync(repo.BasePath, repo.Arch);
                if (result.Success)
                {
                    _logger.LogInformation("Repository metadata updated after publishing package {PackageId}", package.Id);
                }
                else
                {
                    _logger.LogWarning("createrepo_c failed after publishing: {Error}", result.Output);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "createrepo_c failed after publishing package {PackageId}", package.Id);
            }
        }

        _logger.LogInformation("Package {PackageId} published to repository {RepoId}", package.Id, repositoryId);
        return package;
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

        // Save RPM to filesystem
        var savedPath = await _repoManager.SaveRpmAsync(repo.BasePath, repo.Arch, fileName, fileStream);

        // Compute hash
        var hash = _repoManager.ComputeSha256(savedPath);
        var actualSize = new FileInfo(savedPath).Length;

        // Extract metadata from RPM
        string pkgName = Path.GetFileNameWithoutExtension(fileName);
        string pkgVersion = "0.0.0";
        string pkgRelease = "1";
        string pkgArch = repo.Arch;

        var metadata = _repoManager.ExtractRpmMetadata(savedPath);
        if (metadata != null)
        {
            pkgName = metadata.Name;
            pkgVersion = metadata.Version;
            pkgRelease = metadata.Release;
            pkgArch = metadata.Arch;
        }

        var package = new Package
        {
            Id = Guid.NewGuid(),
            RepositoryId = repositoryId,
            ArtifactId = null,
            Name = pkgName,
            Version = pkgVersion,
            Release = pkgRelease,
            Arch = pkgArch,
            FileName = fileName,
            StoragePath = $"{repo.BasePath}/{repo.Arch}/{fileName}",
            FileSize = actualSize,
            HashSha256 = hash,
            PgpSignature = pgpSignature,
            PublishedAt = DateTime.UtcNow,
            PublishedBy = publishedBy
        };

        _db.Packages.Add(package);
        await _db.SaveChangesAsync();

        // Run createrepo_c --update
        try
        {
            var result = await _repoManager.RunCreaterepoAsync(repo.BasePath, repo.Arch);
            if (result.Success)
            {
                _logger.LogInformation("Repository metadata updated after uploading {FileName}", fileName);
            }
            else
            {
                _logger.LogWarning("createrepo_c warning after upload: {Output}", result.Output);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "createrepo_c failed after uploading {FileName}", fileName);
        }

        _logger.LogInformation("Package {FileName} uploaded to repository {RepoId} (id: {PackageId})", fileName, repositoryId, package.Id);
        return package;
    }

    /// <summary>
    /// Synchronizes repository metadata using createrepo_c --update for all architectures.
    /// </summary>
    public async Task SyncRepositoryAsync(Guid repositoryId)
    {
        var repo = await _db.Repositories.FindAsync(repositoryId)
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
                _logger.LogWarning("createrepo_c sync failed: {Output}", result.Output);
            }
        }

        repo.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Repository {RepoName} sync completed", repo.Name);
    }

    public async Task<List<PackageRepository>> ListRepositoriesAsync()
    {
        return await _db.Repositories.OrderByDescending(r => r.CreatedAt).ToListAsync();
    }

    public async Task<List<Package>> ListPackagesAsync(Guid repositoryId)
    {
        return await _db.Packages
            .Where(p => p.RepositoryId == repositoryId)
            .OrderByDescending(p => p.PublishedAt)
            .ToListAsync();
    }
}