using System.Buffers;
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

    Task DownloadArtifactAsync(
        string objectName,
        string destinationPath,
        long expectedBytes,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads runner-staged objects through short-lived MinIO URLs. Callers supply
/// only object names already derived and validated by server-side policy.
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

    public async Task DownloadArtifactAsync(
        string objectName,
        string destinationPath,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        if (expectedBytes <= 0)
            throw new ValidationException("Kubernetes artifact expected size is invalid.");

        using var response = await GetAsync(objectName, cancellationToken);
        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength != expectedBytes)
        {
            throw new ValidationException("Kubernetes artifact object size does not match its manifest.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var copied = await CopyBoundedAsync(source, destination, expectedBytes, cancellationToken);
        if (copied != expectedBytes)
            throw new ValidationException("Kubernetes artifact object size does not match its manifest.");
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
