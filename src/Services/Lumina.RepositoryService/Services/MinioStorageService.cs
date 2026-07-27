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
    /// Publishes a package by fetching the built RPM from BuildService over the
    /// message bus (RepositoryService has no shared filesystem with BuildService
    /// and no view of BuildDbContext), saving it to the repository directory
    /// routed by the package's own architecture, and recording real metadata.
    /// </summary>
    public async Task<Package> PublishPackageAsync(Guid artifactId, Guid repositoryId, string publishedBy)
    {
        var repo = await _db.Repositories.FindAsync(repositoryId)
            ?? throw new NotFoundException($"Repository {repositoryId} not found");

        // Fetch the artifact bytes + real NEVRA filename from BuildService.
        // The previous implementation read a MinIO object (repo-<name>/<id>.rpm)
        // that was never written anywhere in the pipeline, so it always fell
        // into the catch branch and published a zero-byte placeholder under a
        // fabricated name. The bus request is the established pattern — it
        // mirrors the signing-metadata request, the tiny sibling of this lookup.
        ArtifactContent artifact;
        try
        {
            var response = await _bus.Request<GetArtifactContent, ArtifactContent>(
                new GetArtifactContent(artifactId), timeout: TimeSpan.FromSeconds(60));
            artifact = response.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch artifact {ArtifactId} content from BuildService via bus", artifactId);
            throw new ValidationException(
                $"Could not fetch artifact {artifactId} content from BuildService. The build may not have completed or BuildService is unreachable.");
        }

        if (artifact.Content is null || artifact.Content.Length == 0)
        {
            throw new ValidationException(
                $"Artifact {artifactId} has no content to publish (empty payload from BuildService).");
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

        // Save into the repo's default arch dir first (confined); the real arch
        // is only knowable after ExtractRpmMetadata, which requires the file to
        // already live inside the repo tree (it confines its path to the repo
        // root). If the header arch differs, MoveRpm relocates it below.
        var savedPath = _repoManager.SaveRpm(repo.BasePath, repo.Arch, fileName, artifact.Content);

        var metadata = _repoManager.ExtractRpmMetadata(savedPath)
            ?? throw new ValidationException(
                $"Could not read RPM metadata for artifact {artifactId}; the file may be corrupt or not a valid RPM.");

        string pkgName = metadata.Name;
        string pkgVersion = metadata.Version;
        string pkgRelease = metadata.Release;
        string pkgArch = metadata.Arch;
        long pkgSize = metadata.Size > 0 ? metadata.Size : new FileInfo(savedPath).Length;

        // FUNC-003: route by the package's own arch, not the repository's
        // configured arch. A noarch/-devel subpackage must land in its own arch
        // dir so dnf on any client can resolve the full subpackage set.
        if (!string.Equals(pkgArch, repo.Arch, StringComparison.OrdinalIgnoreCase))
        {
            savedPath = _repoManager.MoveRpm(repo.BasePath, repo.Arch, pkgArch, fileName);
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
            StoragePath = $"{repo.BasePath}/{pkgArch}/{fileName}",
            FileSize = pkgSize,
            // Prefer the hash BuildService already computed; fall back to a
            // local recompute as defense-in-depth.
            HashSha256 = !string.IsNullOrWhiteSpace(artifact.HashSha256)
                ? artifact.HashSha256
                : _repoManager.ComputeSha256(savedPath),
            SigningKeyFingerprint = signing.KeyFingerprint,
            PublishedAt = DateTime.UtcNow,
            PublishedBy = publishedBy
        };

        _db.Packages.Add(package);
        await _db.SaveChangesAsync();

        // FUNC-006: regenerate metadata for the arch dir that actually received
        // the file (the package's own arch), not just the repository's default.
        await RunCreaterepoForArchAsync(repo.BasePath, pkgArch, package.Id);

        _logger.LogInformation("Package {PackageId} published to repository {RepoId} (arch {Arch})", package.Id, repositoryId, pkgArch);
        return package;
    }

    /// <summary>
    /// Runs createrepo_c on a single arch directory after a publish/upload,
    /// logging failures without swallowing them silently. Extracted so both
    /// publish paths route metadata regeneration through the package's own arch.
    /// </summary>
    private async Task RunCreaterepoForArchAsync(string basePath, string arch, Guid packageId)
    {
        try
        {
            var result = await _repoManager.RunCreaterepoAsync(basePath, arch);
            if (result.Success)
            {
                _logger.LogInformation("Repository metadata updated for {Arch} after publishing package {PackageId}", arch, packageId);
            }
            else
            {
                _logger.LogWarning("createrepo_c failed after publishing package {PackageId}: {Error}", packageId, result.Output);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "createrepo_c threw after publishing package {PackageId}", packageId);
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

        // Save RPM to filesystem (default arch dir first; relocated below if the
        // RPM header declares a different arch — same routing logic as publish).
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

        // FUNC-003: route by the package's own arch, not the repository's.
        if (!string.Equals(pkgArch, repo.Arch, StringComparison.OrdinalIgnoreCase))
        {
            savedPath = _repoManager.MoveRpm(repo.BasePath, repo.Arch, pkgArch, fileName);
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
            StoragePath = $"{repo.BasePath}/{pkgArch}/{fileName}",
            FileSize = actualSize,
            HashSha256 = hash,
            PgpSignature = pgpSignature,
            PublishedAt = DateTime.UtcNow,
            PublishedBy = publishedBy
        };

        _db.Packages.Add(package);
        await _db.SaveChangesAsync();

        // FUNC-006: regenerate metadata for the arch dir that received the file.
        await RunCreaterepoForArchAsync(repo.BasePath, pkgArch, package.Id);

        _logger.LogInformation("Package {FileName} uploaded to repository {RepoId} (arch {Arch}, id: {PackageId})", fileName, repositoryId, pkgArch, package.Id);
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
