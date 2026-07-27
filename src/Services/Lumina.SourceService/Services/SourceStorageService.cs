using Minio;
using Minio.DataModel.Args;

namespace Lumina.SourceService.Services;

/// <summary>
/// Manages storage of fetched source archives in MinIO.
/// Bucket: "lumina-sources"
/// Path pattern: {packageName}/{filename}
/// </summary>
public class SourceStorageService
{
    private readonly IMinioClient _minio;
    private readonly ILogger<SourceStorageService> _logger;
    private const string BucketName = "lumina-sources";
    private bool _bucketEnsured;

    public SourceStorageService(ILogger<SourceStorageService> logger, IConfiguration config)
    {
        _logger = logger;
        var endpoint = config["MinIO:Endpoint"] ?? "minio:9000";
        var accessKey = config["MinIO:AccessKey"] ?? "luminaadmin";
        var secretKey = config["MinIO:SecretKey"] ?? "CHANGE_ME";

        _minio = new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey)
            .Build();
    }

    /// <summary>
    /// Upload a source archive to MinIO. Returns the storage path.
    /// </summary>
    public async Task<string> UploadAsync(
        string packageName,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await EnsureBucketAsync(cancellationToken);

        var fileName = Path.GetFileName(filePath);
        var objectName = $"{packageName}/{fileName}";

        _logger.LogInformation("Uploading {File} to MinIO: {Object}", fileName, objectName);

        var putArgs = new PutObjectArgs()
            .WithBucket(BucketName)
            .WithObject(objectName)
            .WithFileName(filePath)
            .WithContentType(GetContentType(fileName));

        await _minio.PutObjectAsync(putArgs, cancellationToken);

        _logger.LogInformation("Uploaded {Object} to MinIO successfully", objectName);
        return objectName;
    }

    /// <summary>
    /// Download a source archive from MinIO to a local path. Returns the local file path.
    /// </summary>
    public async Task<string> DownloadAsync(string packageName, string localDir)
    {
        await EnsureBucketAsync();

        // Find the latest archive for this package
        var listArgs = new ListObjectsArgs()
            .WithBucket(BucketName)
            .WithPrefix($"{packageName}/")
            .WithRecursive(false);

        string? latestObject = null;
        DateTime? latestTime = null;

        await foreach (var item in _minio.ListObjectsEnumAsync(listArgs))
        {
            if (latestTime == null || item.LastModifiedDateTime > latestTime)
            {
                latestObject = item.Key;
                latestTime = item.LastModifiedDateTime;
            }
        }

        if (latestObject == null)
            throw new Exception($"No source archive found for package: {packageName}");

        Directory.CreateDirectory(localDir);
        var localPath = Path.Combine(localDir, Path.GetFileName(latestObject));

        var getArgs = new GetObjectArgs()
            .WithBucket(BucketName)
            .WithObject(latestObject)
            .WithFile(localPath);

        await _minio.GetObjectAsync(getArgs);

        _logger.LogInformation("Downloaded {Object} from MinIO to {Path}", latestObject, localPath);
        return localPath;
    }

    /// <summary>
    /// Get a presigned URL for downloading the source archive (valid for 1 hour).
    /// </summary>
    public async Task<string?> GetDownloadUrlAsync(string packageName)
    {
        await EnsureBucketAsync();

        // Find the latest archive for this package
        var listArgs = new ListObjectsArgs()
            .WithBucket(BucketName)
            .WithPrefix($"{packageName}/")
            .WithRecursive(false);

        string? latestObject = null;

        await foreach (var item in _minio.ListObjectsEnumAsync(listArgs))
        {
            latestObject = item.Key; // Last item from listing
        }

        if (latestObject == null)
            return null;

        var presignArgs = new PresignedGetObjectArgs()
            .WithBucket(BucketName)
            .WithObject(latestObject)
            .WithExpiry(3600);

        return await _minio.PresignedGetObjectAsync(presignArgs);
    }

    /// <summary>
    /// Check if a source archive exists for a package
    /// </summary>
    public async Task<bool> ExistsAsync(string packageName)
    {
        await EnsureBucketAsync();

        try
        {
            var listArgs = new ListObjectsArgs()
                .WithBucket(BucketName)
                .WithPrefix($"{packageName}/")
                .WithRecursive(false);

            await foreach (var _ in _minio.ListObjectsEnumAsync(listArgs))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureBucketAsync(CancellationToken cancellationToken = default)
    {
        if (_bucketEnsured) return;

        try
        {
            var bucketExistsArgs = new BucketExistsArgs().WithBucket(BucketName);
            var exists = await _minio.BucketExistsAsync(bucketExistsArgs, cancellationToken);

            if (!exists)
            {
                var makeBucketArgs = new MakeBucketArgs().WithBucket(BucketName);
                await _minio.MakeBucketAsync(makeBucketArgs, cancellationToken);
                _logger.LogInformation("Created MinIO bucket: {Bucket}", BucketName);
            }

            _bucketEnsured = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure MinIO bucket {Bucket}", BucketName);
            throw;
        }
    }

    private static string GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".gz" or ".tgz" => "application/gzip",
            ".bz2" or ".tbz2" => "application/x-bzip2",
            ".xz" or ".txz" => "application/x-xz",
            ".tar" => "application/x-tar",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }
}
