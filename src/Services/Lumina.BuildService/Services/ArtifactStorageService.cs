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
    private readonly HttpClient _artifactHttpClient;

    public ArtifactStorageService(
        IMinioClient minio,
        ILogger<ArtifactStorageService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _minio = minio;
        _logger = logger;
        _artifactHttpClient = httpClientFactory.CreateClient("ArtifactStorage");
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

        var snapshotDirectory = Path.Combine(
            Path.GetTempPath(), "lumina-artifact-snapshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshotDirectory);
        var snapshotPath = Path.Combine(snapshotDirectory, safeName);
        try
        {
            await using (var source = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            await using var stream = new FileStream(
                snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != expectedSize)
                throw new ValidationException("Signed artifact size does not match the signing result.");

            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
            if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("Signed artifact digest does not match the signing result.");

            stream.Position = 0;
            var objectName = $"sha256/{actualHash}/{safeName}";

            await EnsureBucketAsync(cancellationToken);
            var uploadUrl = await _minio.PresignedPutObjectAsync(
                new PresignedPutObjectArgs()
                    .WithBucket(BucketName)
                    .WithObject(objectName)
                    .WithExpiry(120));
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
            {
                Content = new StreamContent(stream)
            };
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-rpm");
            request.Content.Headers.ContentLength = stream.Length;
            using var response = await _artifactHttpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation(
                "Stored signed artifact {FileName} as immutable object {ObjectName}",
                safeName, objectName);
            return objectName;
        }
        finally
        {
            if (Directory.Exists(snapshotDirectory))
                Directory.Delete(snapshotDirectory, recursive: true);
        }
    }

    public async Task EnsureBucketAsync(CancellationToken cancellationToken = default)
    {
        // MinIO .NET 6 can incorrectly report a non-existent bucket as present
        // once another bucket exists on the endpoint. Creation is idempotent:
        // try it first, then accept an error only when a follow-up HEAD proves
        // that another caller already created the bucket.
        try
        {
            await _minio.MakeBucketAsync(
                new MakeBucketArgs().WithBucket(BucketName), cancellationToken);
        }
        catch
        {
            var exists = await _minio.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(BucketName), cancellationToken);
            if (!exists)
                throw;
        }
    }
}
