using Lumina.Shared.Errors;
using Minio;
using Minio.DataModel.Args;

namespace Lumina.BuildService.Services.PackageGraph;

public interface IRepositorySnapshotStreamProvider
{
    Task<RepositorySnapshotStream> OpenAsync(
        Guid projectId,
        string storagePath,
        string expectedSha256,
        long expectedSize,
        CancellationToken cancellationToken);
}

public sealed class RepositorySnapshotStream : IAsyncDisposable
{
    private readonly HttpResponseMessage? _response;

    public RepositorySnapshotStream(Stream stream, HttpResponseMessage? response = null)
    {
        Stream = stream;
        _response = response;
    }

    public Stream Stream { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Stream.DisposeAsync();
        }
        finally
        {
            _response?.Dispose();
        }
    }
}

public sealed class RepositorySnapshotStreamProvider : IRepositorySnapshotStreamProvider
{
    public const string BucketName = "lumina-sources";
    private readonly IMinioClient _minio;
    private readonly HttpClient _httpClient;

    public RepositorySnapshotStreamProvider(
        IMinioClient minio,
        IHttpClientFactory httpClientFactory)
    {
        _minio = minio;
        _httpClient = httpClientFactory.CreateClient("RepositorySnapshots");
    }

    public async Task<RepositorySnapshotStream> OpenAsync(
        Guid projectId,
        string storagePath,
        string expectedSha256,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        ValidateObjectIdentity(projectId, storagePath, expectedSha256, expectedSize);
        var url = await _minio.PresignedGetObjectAsync(new PresignedGetObjectArgs()
            .WithBucket(BucketName)
            .WithObject(storagePath)
            .WithExpiry(120));
        var response = await _httpClient.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } contentLength &&
                contentLength != expectedSize)
            {
                throw new ValidationException(
                    "Repository snapshot HTTP size does not match immutable metadata.");
            }
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new RepositorySnapshotStream(stream, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public static void ValidateObjectIdentity(
        Guid projectId,
        string storagePath,
        string expectedSha256,
        long expectedSize)
    {
        if (expectedSha256.Length != 64 || expectedSha256.Any(character => !Uri.IsHexDigit(character)) ||
            expectedSize <= 0)
        {
            throw new ValidationException("Repository snapshot immutable metadata is invalid.");
        }
        var segments = storagePath.Split('/');
        var expectedPrefix = $"project-{projectId:N}";
        if (segments.Length != 3 ||
            !string.Equals(segments[0], expectedPrefix, StringComparison.Ordinal) ||
            !string.Equals(segments[1], expectedSha256, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(segments[2]) ||
            !segments[2].EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            storagePath.Any(char.IsControl))
        {
            throw new ValidationException(
                "Repository snapshot object path is not the expected content-addressed project object.");
        }
    }
}
