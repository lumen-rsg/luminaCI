using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lumina.Shared.DTOs;

namespace Lumina.WebApp.Services;

public class LuminaApiService
{
    private readonly HttpClient _http;
    private readonly ILogger<LuminaApiService> _logger;

    private static readonly HashSet<int> TransientStatusCodes = [502, 503, 504];
    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4)
    ];

    public LuminaApiService(HttpClient http, ILogger<LuminaApiService> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Executes an HTTP request with automatic retry for transient failures (502, 503, 504).
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<Task<HttpResponseMessage>> sendFunc, string requestLabel)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await sendFunc();

                if (response.IsSuccessStatusCode || !TransientStatusCodes.Contains((int)response.StatusCode))
                    return response;

                var status = (int)response.StatusCode;
                if (attempt < MaxRetries - 1)
                {
                    _logger.LogWarning(
                        "Transient {Status} for {Label}, retrying ({Attempt}/{Max})...",
                        status, requestLabel, attempt + 1, MaxRetries);
                    await Task.Delay(RetryDelays[attempt]);
                    continue;
                }

                _logger.LogError(
                    "Transient {Status} for {Label} persisted after {Max} retries",
                    status, requestLabel, MaxRetries);
                return response;
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries - 1)
            {
                _logger.LogWarning(ex,
                    "Connection error for {Label}, retrying ({Attempt}/{Max})...",
                    requestLabel, attempt + 1, MaxRetries);
                await Task.Delay(RetryDelays[attempt]);
            }
        }
    }

    /// <summary>
    /// GET with retry, preserving HTTP status and the API error envelope.
    /// </summary>
    private async Task<T?> GetJsonWithRetryAsync<T>(string url, string label)
        => await SendAndReadJsonAsync<T>(() => _http.GetAsync(url), label, retry: true);

    private static string? TryReadApiError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var json = JsonDocument.Parse(body);
            foreach (var name in new[] { "error", "message" })
            {
                if (json.RootElement.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>
    /// Sends a request and applies one failure contract to every API method:
    /// non-success status, malformed JSON, and empty bodies all throw
    /// <see cref="ApiRequestException"/> with the server error envelope when
    /// available. Only safe GET requests opt into transient retries.
    /// </summary>
    private async Task<T?> SendAndReadJsonAsync<T>(
        Func<Task<HttpResponseMessage>> sendFunc,
        string label,
        bool retry = false)
    {
        using var response = retry
            ? await SendWithRetryAsync(sendFunc, label)
            : await sendFunc();

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            var message = TryReadApiError(body) ?? response.ReasonPhrase ?? "Request failed";
            _logger.LogWarning(
                "{Label} returned {Status}: {Message}",
                label, (int)response.StatusCode, message);
            throw new ApiRequestException(response.StatusCode, message);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>()
                ?? throw new ApiRequestException(
                    response.StatusCode, "The server returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new ApiRequestException(
                response.StatusCode,
                $"The server returned invalid JSON for {label}.",
                ex);
        }
    }

    // === Builds ===
    public async Task<ApiResponse<BuildListResponse>?> GetBuildsAsync(
        int page = 1, int pageSize = 20, Lumina.Shared.Models.Enums.BuildStatus? status = null)
    {
        var statusQuery = status.HasValue ? $"&status={status.Value}" : "";
        return await GetJsonWithRetryAsync<ApiResponse<BuildListResponse>>(
            $"/api/builds?page={page}&pageSize={pageSize}{statusQuery}", nameof(GetBuildsAsync));
    }

    public async Task<ApiResponse<BuildStatsResponse>?> GetBuildStatsAsync() =>
        await GetJsonWithRetryAsync<ApiResponse<BuildStatsResponse>>(
            "/api/builds/stats", nameof(GetBuildStatsAsync));

    public async Task<ApiResponse<object>?> CancelBuildAsync(Guid buildId)
    {
        return await SendAndReadJsonAsync<ApiResponse<object>>(
            () => _http.PostAsync($"/api/builds/{buildId}/cancel", null), nameof(CancelBuildAsync));
    }

    public async Task<ApiResponse<object>?> ClearBuildQueueAsync()
    {
        return await SendAndReadJsonAsync<ApiResponse<object>>(
            () => _http.DeleteAsync("/api/builds/queue/clear"), nameof(ClearBuildQueueAsync));
    }

    public async Task<ApiResponse<BuildQueueResponse>?> GetBuildQueueAsync()
    {
        return await GetJsonWithRetryAsync<ApiResponse<BuildQueueResponse>>(
            "/api/builds/queue", nameof(GetBuildQueueAsync));
    }

    public async Task<ApiResponse<BuildJobResponse>?> GetBuildAsync(Guid id)
    {
        return await GetJsonWithRetryAsync<ApiResponse<BuildJobResponse>>(
            $"/api/builds/{id}", nameof(GetBuildAsync));
    }

    public async Task<ApiResponse<string>?> GetBuildLogsAsync(Guid id)
    {
        return await GetJsonWithRetryAsync<ApiResponse<string>>(
            $"/api/builds/{id}/logs", nameof(GetBuildLogsAsync));
    }

    // === Pipelines ===
    public async Task<ApiResponse<PipelineListResponse>?> GetPipelinesAsync(
        int page = 1, int pageSize = 20, string? search = null)
    {
        var searchQuery = string.IsNullOrWhiteSpace(search)
            ? ""
            : $"&search={Uri.EscapeDataString(search.Trim())}";
        return await GetJsonWithRetryAsync<ApiResponse<PipelineListResponse>>(
            $"/api/pipelines?page={page}&pageSize={pageSize}{searchQuery}", nameof(GetPipelinesAsync));
    }

    public async Task<ApiResponse<PipelineResponse>?> GetPipelineAsync(Guid id)
    {
        return await GetJsonWithRetryAsync<ApiResponse<PipelineResponse>>(
            $"/api/pipelines/{id}", nameof(GetPipelineAsync));
    }

    public async Task<ApiResponse<PipelineResponse>?> CreatePipelineAsync(CreatePipelineRequest request)
        => await SendAndReadJsonAsync<ApiResponse<PipelineResponse>>(
            () => _http.PostAsJsonAsync("/api/pipelines", request), nameof(CreatePipelineAsync));

    public async Task<ApiResponse<PipelineResponse>?> UpdatePipelineAsync(Guid id, UpdatePipelineRequest request)
        => await SendAndReadJsonAsync<ApiResponse<PipelineResponse>>(
            () => _http.PutAsJsonAsync($"/api/pipelines/{id}", request), nameof(UpdatePipelineAsync));

    public async Task<ApiResponse<object>?> DeletePipelineAsync(Guid id)
        => await SendAndReadJsonAsync<ApiResponse<object>>(
            () => _http.DeleteAsync($"/api/pipelines/{id}"), nameof(DeletePipelineAsync));

    public async Task<ApiResponse<BuildJobResponse>?> TriggerPipelineAsync(Guid pipelineId)
        => await SendAndReadJsonAsync<ApiResponse<BuildJobResponse>>(
            () => _http.PostAsync($"/api/pipelines/{pipelineId}/trigger", null), nameof(TriggerPipelineAsync));

    public async Task<ApiResponse<BuildJobResponse>?> TriggerPipelineWithRequestAsync(Guid pipelineId, TriggerBuildRequest request)
        => await SendAndReadJsonAsync<ApiResponse<BuildJobResponse>>(
            () => _http.PostAsJsonAsync($"/api/pipelines/{pipelineId}/trigger", request),
            nameof(TriggerPipelineWithRequestAsync));

    public async Task<ApiResponse<BuildJobResponse>?> TriggerAutoBuildAsync(Guid pipelineId, string triggeredBy = "auto")
        => await SendAndReadJsonAsync<ApiResponse<BuildJobResponse>>(
            () => _http.PostAsJsonAsync(
                $"/api/pipelines/{pipelineId}/trigger-auto",
                new TriggerAutoBuildRequest(triggeredBy)),
            nameof(TriggerAutoBuildAsync));

    // === Security ===
    public async Task<ApiResponse<HashListResponse>?> GetHashRecordsAsync(int page = 1, int pageSize = 20)
    {
        return await GetJsonWithRetryAsync<ApiResponse<HashListResponse>>(
            $"/api/security/hashes?page={page}&pageSize={pageSize}", nameof(GetHashRecordsAsync));
    }

    public async Task<ApiResponse<object>?> GenerateKeyAsync(string keyName, string email)
        => await SendAndReadJsonAsync<ApiResponse<object>>(
            () => _http.PostAsJsonAsync(
                "/api/security/keys/generate",
                new GenerateKeyRequest(keyName, email)),
            nameof(GenerateKeyAsync));

    public async Task<ApiResponse<List<object>>?> GetKeysAsync()
        => await GetJsonWithRetryAsync<ApiResponse<List<object>>>(
            "/api/security/keys", nameof(GetKeysAsync));

    // === Scanner ===
    // The backend GET /api/scanner/scans returns ScanPaginatedResponse (a list of
    // ScanSummaryResponse). The DTO must match: previously this deserialised into
    // ScanListResponse/ScanResponse, which left fields like CreatedAt unbound and
    // rendered default(DateTime) in the UI.
    public async Task<ApiResponse<ScanPaginatedResponse>?> GetScansAsync(int page = 1, int pageSize = 20)
    {
        return await GetJsonWithRetryAsync<ApiResponse<ScanPaginatedResponse>>(
            $"/api/scanner/scans?page={page}&pageSize={pageSize}", nameof(GetScansAsync));
    }

    // === Repositories ===
    public async Task<ApiResponse<RepositoryListResponse>?> GetRepositoriesAsync()
    {
        return await GetJsonWithRetryAsync<ApiResponse<RepositoryListResponse>>(
            "/api/repository", nameof(GetRepositoriesAsync));
    }

    public async Task<ApiResponse<RepositoryResponse>?> CreateRepositoryAsync(CreateRepositoryRequest request)
        => await SendAndReadJsonAsync<ApiResponse<RepositoryResponse>>(
            () => _http.PostAsJsonAsync("/api/repository", request), nameof(CreateRepositoryAsync));

    public async Task<ApiResponse<PackageResponse>?> UploadPackageAsync(Guid repositoryId, Stream fileStream, string fileName, Stream signatureStream, string signatureFileName, string publishedBy = "upload")
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-rpm");
        content.Add(fileContent, "file", fileName);
        var sigContent = new StreamContent(signatureStream);
        sigContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pgp-signature");
        content.Add(sigContent, "signature", signatureFileName);
        content.Add(new StringContent(repositoryId.ToString()), "repositoryId");
        content.Add(new StringContent(publishedBy), "publishedBy");

        return await SendAndReadJsonAsync<ApiResponse<PackageResponse>>(
            () => _http.PostAsync("/api/repository/upload", content), nameof(UploadPackageAsync));
    }

    public async Task<ApiResponse<object>?> SyncRepositoryAsync(Guid repositoryId)
        => await SendAndReadJsonAsync<ApiResponse<object>>(
            () => _http.PostAsJsonAsync(
                "/api/repository/sync",
                new SyncRepositoryRequest(repositoryId)),
            nameof(SyncRepositoryAsync));

    public async Task<ApiResponse<List<PackageResponse>>?> GetRepositoryPackagesAsync(Guid repositoryId)
        => await GetJsonWithRetryAsync<ApiResponse<List<PackageResponse>>>(
            $"/api/repository/{repositoryId}/packages", nameof(GetRepositoryPackagesAsync));

    public async Task<ApiResponse<PackageResponse>?> PublishPackageAsync(Guid artifactId, Guid repositoryId, string publishedBy = "build-service")
        => await SendAndReadJsonAsync<ApiResponse<PackageResponse>>(
            () => _http.PostAsJsonAsync(
                "/api/repository/publish",
                new PublishPackageRequest(artifactId, repositoryId, publishedBy)),
            nameof(PublishPackageAsync));

    // === Sources ===
    public async Task<ApiResponse<SourceListResponse>?> GetSourcesAsync()
        => await GetJsonWithRetryAsync<ApiResponse<SourceListResponse>>(
            "/api/sources", nameof(GetSourcesAsync));

    public async Task<ApiResponse<SourcePackageResponse>?> GetSourceAsync(string name)
        => await GetJsonWithRetryAsync<ApiResponse<SourcePackageResponse>>(
            $"/api/sources/{Uri.EscapeDataString(name)}", nameof(GetSourceAsync));

    public async Task<ApiResponse<SourceFetchResponse>?> FetchSourceAsync(string name)
        => await SendAndReadJsonAsync<ApiResponse<SourceFetchResponse>>(
            () => _http.PostAsync($"/api/sources/{Uri.EscapeDataString(name)}/fetch", null),
            nameof(FetchSourceAsync));

    public async Task<ApiResponse<SourcePackageMutationResponse>?> CreateSourceAsync(
        SavePackageSourceRequest request)
        => await SendAndReadJsonAsync<ApiResponse<SourcePackageMutationResponse>>(
            () => _http.PostAsJsonAsync("/api/sources", request),
            nameof(CreateSourceAsync));

    public async Task<ApiResponse<SourcePackageMutationResponse>?> UpdateSourceAsync(
        string name,
        SavePackageSourceRequest request)
        => await SendAndReadJsonAsync<ApiResponse<SourcePackageMutationResponse>>(
            () => _http.PutAsJsonAsync(
                $"/api/sources/{Uri.EscapeDataString(name)}", request),
            nameof(UpdateSourceAsync));

    public async Task<ApiResponse<object>?> DisableSourceAsync(
        string name,
        int expectedRevision)
        => await SendAndReadJsonAsync<ApiResponse<object>>(
            () => _http.DeleteAsync(
                $"/api/sources/{Uri.EscapeDataString(name)}?expectedRevision={expectedRevision}"),
            nameof(DisableSourceAsync));

    // === Extra Sources (pipeline & build level) ===
    public async Task<ApiResponse<List<UploadedSourceResponse>>?> GetPipelineSourcesAsync(Guid pipelineId)
    {
        return await GetJsonWithRetryAsync<ApiResponse<List<UploadedSourceResponse>>>(
            $"/api/extra-sources/pipeline/{pipelineId}", nameof(GetPipelineSourcesAsync));
    }

    public async Task<ApiResponse<List<UploadedSourceResponse>>?> UploadPipelineSourceAsync(
        Guid pipelineId, Stream fileStream, string fileName, string? subFolder = null,
        IProgress<(long Uploaded, long Total)>? progress = null)
    {
        var totalBytes = fileStream.CanSeek ? fileStream.Length : -1;
        Stream uploadStream = progress != null && totalBytes > 0
            ? new ProgressStream(fileStream, totalBytes, progress)
            : fileStream;

        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(uploadStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "files", fileName);
        if (!string.IsNullOrEmpty(subFolder))
            content.Add(new StringContent(subFolder), "subFolder");

        return await SendAndReadJsonAsync<ApiResponse<List<UploadedSourceResponse>>>(
            () => _http.PostAsync($"/api/extra-sources/pipeline/{pipelineId}", content),
            nameof(UploadPipelineSourceAsync));
    }

    /// <summary>
    /// Wraps a Stream and reports read progress via IProgress.
    /// </summary>
    private class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _total;
        private readonly IProgress<(long Uploaded, long Total)> _progress;
        private long _bytesRead;
        private long _lastReported;

        public ProgressStream(Stream inner, long total, IProgress<(long Uploaded, long Total)> progress)
        {
            _inner = inner;
            _total = total;
            _progress = progress;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            ReportProgress(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
            ReportProgress(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            ReportProgress(read);
            return read;
        }

        private void ReportProgress(int bytesRead)
        {
            if (bytesRead <= 0) return;
            _bytesRead += bytesRead;
            // Throttle progress reports to ~1% increments
            var pct = _bytesRead * 100 / _total;
            var lastPct = _lastReported * 100 / _total;
            if (pct != lastPct || _bytesRead >= _total)
            {
                _lastReported = _bytesRead;
                _progress.Report((_bytesRead, _total));
            }
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() => _inner.Flush();
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { _inner.Dispose(); base.Dispose(disposing); }
    }

    public async Task<ApiResponse<object>?> DeletePipelineSourceAsync(Guid pipelineId, string filePath)
        => await SendAndReadJsonAsync<ApiResponse<object>>(
            () => _http.DeleteAsync(
                $"/api/extra-sources/pipeline/{pipelineId}/{Uri.EscapeDataString(filePath)}"),
            nameof(DeletePipelineSourceAsync));

    public async Task<ApiResponse<object>?> ClearPipelineSourcesAsync(Guid pipelineId)
        => await SendAndReadJsonAsync<ApiResponse<object>>(
            () => _http.DeleteAsync($"/api/extra-sources/pipeline/{pipelineId}"),
            nameof(ClearPipelineSourcesAsync));
}

public sealed class ApiRequestException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public ApiRequestException(HttpStatusCode statusCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
