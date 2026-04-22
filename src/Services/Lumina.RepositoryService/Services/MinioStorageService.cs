using Lumina.RepositoryService.Data;
using Lumina.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;

namespace Lumina.RepositoryService.Services;

public class MinioStorageService
{
    private readonly RepositoryDbContext _db;
    private readonly IMinioClient _minio;
    private readonly ILogger<MinioStorageService> _logger;

    public MinioStorageService(RepositoryDbContext db, IMinioClient minio, ILogger<MinioStorageService> logger)
    {
        _db = db;
        _minio = minio;
        _logger = logger;
    }

    public async Task<PackageRepository> CreateRepositoryAsync(string name, string displayName, string basePath, string arch, string distribution, string createdBy)
    {
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

        _db.Repositories.Add(repo);
        await _db.SaveChangesAsync();
        return repo;
    }

    public async Task<Package> PublishPackageAsync(Guid artifactId, Guid repositoryId, string publishedBy)
    {
        var repo = await _db.Repositories.FindAsync(repositoryId)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found");

        var package = new Package
        {
            Id = Guid.NewGuid(),
            RepositoryId = repositoryId,
            ArtifactId = artifactId,
            Name = $"package-{artifactId:N}",
            Version = "1.0.0",
            Release = "1",
            Arch = repo.Arch,
            FileName = $"package-{artifactId:N}.rpm",
            StoragePath = $"{repo.BasePath}/{artifactId:N}.rpm",
            FileSize = 0,
            PublishedAt = DateTime.UtcNow,
            PublishedBy = publishedBy
        };

        _db.Packages.Add(package);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Package {PackageId} published to repository {RepoId}", package.Id, repositoryId);
        return package;
    }

    public async Task SyncRepositoryAsync(Guid repositoryId)
    {
        var repo = await _db.Repositories.FindAsync(repositoryId)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found");

        // Generate repository metadata (createrepo equivalent)
        _logger.LogInformation("Syncing repository {RepoName}", repo.Name);
        repo.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
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