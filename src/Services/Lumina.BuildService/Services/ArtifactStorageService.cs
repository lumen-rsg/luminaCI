using System.Security.Cryptography;
using Lumina.Shared.Errors;
using Minio;
using Minio.DataModel.Args;

namespace Lumina.BuildService.Services;

/// <summary>
/// Persists final signed RPMs as immutable, content-addressed objects.
/// </summary>
public class ArtifactStorageService
{
    public const string BucketName = "lumina-artifacts";

    private readonly IMinioClient _minio;
    private readonly ILogger<ArtifactStorageService> _logger;

    public ArtifactStorageService(IMinioClient minio, ILogger<ArtifactStorageService> logger)
    {
        _minio = minio;
        _logger = logger;
    }

    public async Task<string> UploadSignedArtifactAsync(
        string filePath,
        string fileName,
        string expectedSha256,
        long expectedSize,
        CancellationToken cancellationToken = default)
    {
        var safeName = Path.GetFileName(fileName);
        if (safeName != fileName || !safeName.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("Artifact filename must be a plain .rpm filename.");
        if (expectedSha256.Length != 64 || expectedSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new ValidationException("Artifact SHA-256 digest is invalid.");

        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedSize)
            throw new ValidationException("Signed artifact size does not match the signing result.");

        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("Signed artifact digest does not match the signing result.");

        stream.Position = 0;
        var normalizedHash = actualHash.ToLowerInvariant();
        var objectName = $"sha256/{normalizedHash}/{safeName}";

        await EnsureBucketAsync(cancellationToken);
        await _minio.PutObjectAsync(
            new PutObjectArgs()
                .WithBucket(BucketName)
                .WithObject(objectName)
                .WithStreamData(stream)
                .WithObjectSize(stream.Length)
                .WithContentType("application/x-rpm"),
            cancellationToken);

        _logger.LogInformation(
            "Stored signed artifact {FileName} as immutable object {ObjectName}",
            safeName, objectName);
        return objectName;
    }

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        var exists = await _minio.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(BucketName), cancellationToken);
        if (!exists)
        {
            await _minio.MakeBucketAsync(
                new MakeBucketArgs().WithBucket(BucketName), cancellationToken);
        }
    }
}
