using System.Buffers;
using System.Security.Cryptography;
using Lumina.Shared.Errors;
using Minio;
using Minio.DataModel.Args;

namespace Lumina.BuildService.Services;

internal interface IKubernetesArtifactObjectStore
{
    Task<byte[]> ReadManifestAsync(
        string objectName,
        int maximumBytes,
        CancellationToken cancellationToken);

    Task DownloadBundleAsync(
        string objectName,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken);

    Task UploadVerifiedArtifactAsync(
        string objectName,
        string sourcePath,
        long expectedBytes,
        string expectedSha256,
        CancellationToken cancellationToken);

    Task UploadManifestAsync(
        string objectName,
        byte[] manifestBytes,
        CancellationToken cancellationToken);
}

/// <summary>
/// Moves runner-staged objects across the trusted BuildService boundary through
/// short-lived MinIO URLs. Callers supply only object names already derived and
/// validated by server-side policy.
/// </summary>
internal sealed class KubernetesArtifactObjectStore : IKubernetesArtifactObjectStore
{
    private readonly IMinioClient _minio;
    private readonly HttpClient _httpClient;

    public KubernetesArtifactObjectStore(
        IMinioClient minio,
        IHttpClientFactory httpClientFactory)
    {
        _minio = minio;
        _httpClient = httpClientFactory.CreateClient("KubernetesArtifacts");
    }

    public async Task<byte[]> ReadManifestAsync(
        string objectName,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        using var response = await GetAsync(objectName, cancellationToken);
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new ValidationException("Kubernetes artifact manifest exceeds the size limit.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        await CopyBoundedAsync(source, destination, maximumBytes, cancellationToken);
        return destination.ToArray();
    }

    public async Task DownloadBundleAsync(
        string objectName,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0)
            throw new ValidationException("Kubernetes artifact bundle size limit is invalid.");

        using var response = await GetAsync(objectName, cancellationToken);
        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength > maximumBytes)
        {
            throw new ValidationException("Kubernetes artifact bundle exceeds the size limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var copied = await CopyBoundedAsync(source, destination, maximumBytes, cancellationToken);
        if (copied == 0)
            throw new ValidationException("Kubernetes artifact bundle is empty.");
    }

    public async Task UploadVerifiedArtifactAsync(
        string objectName,
        string sourcePath,
        long expectedBytes,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (expectedBytes <= 0 ||
            expectedSha256.Length != 64 ||
            expectedSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ValidationException("Verified Kubernetes artifact metadata is invalid.");
        }

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != expectedBytes)
            throw new ValidationException("Verified Kubernetes artifact size changed before upload.");
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken))
            .ToLowerInvariant();
        if (!string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("Verified Kubernetes artifact digest changed before upload.");
        source.Position = 0;
        await PutAsync(
            objectName,
            new StreamContent(source),
            "application/x-rpm",
            expectedBytes,
            cancellationToken);
    }

    public Task UploadManifestAsync(
        string objectName,
        byte[] manifestBytes,
        CancellationToken cancellationToken)
    {
        if (manifestBytes is not { Length: > 0 and <= KubernetesArtifactBundleReader.MaximumManifestBytes })
            throw new ValidationException("Kubernetes artifact manifest size is invalid.");
        return PutAsync(
            objectName,
            new ByteArrayContent(manifestBytes),
            "application/json",
            manifestBytes.LongLength,
            cancellationToken);
    }

    private async Task<HttpResponseMessage> GetAsync(
        string objectName,
        CancellationToken cancellationToken)
    {
        var url = await _minio.PresignedGetObjectAsync(
            new PresignedGetObjectArgs()
                .WithBucket(ArtifactStorageService.BucketName)
                .WithObject(objectName)
                .WithExpiry(120));
        var response = await _httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task PutAsync(
        string objectName,
        HttpContent content,
        string mediaType,
        long contentLength,
        CancellationToken cancellationToken)
    {
        using (content)
        {
            var url = await _minio.PresignedPutObjectAsync(
                new PresignedPutObjectArgs()
                    .WithBucket(ArtifactStorageService.BucketName)
                    .WithObject(objectName)
                    .WithExpiry(120));
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            content.Headers.ContentLength = contentLength;
            using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    internal static async Task<long> CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                    return total;
                total = checked(total + read);
                if (total > maximumBytes)
                    throw new ValidationException("Kubernetes artifact object exceeds the size limit.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch (OverflowException exception)
        {
            throw new ValidationException("Kubernetes artifact object size overflowed.", exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
