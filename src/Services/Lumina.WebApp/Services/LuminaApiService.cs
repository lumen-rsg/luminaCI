using System.Net;
using System.Net.Http.Json;
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
    /// GET with retry, then deserialise. Returns null on non-success (including 401).
    /// </summary>
    private async Task<T?> GetJsonWithRetryAsync<T>(string url, string label)
    {
        var response = await SendWithRetryAsync(
            () => _http.GetAsync(url), label);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("{Label} returned {Status}, returning null", label, (int)response.StatusCode);
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>();
    }

    /// <summary>
    /// Sends a request with retry and deserialises the JSON body.
    /// Returns null-deserialised result on failure instead of throwing.
    /// </summary>
    private async Task<T?> SendAndReadJsonAsync<T>(
        Func<Task<HttpResponseMessage>> sendFunc, string label)
    {
        var response = await SendWithRetryAsync(sendFunc, label);
        return await response.Content.ReadFromJsonAsync<T>();
    }

    // === Builds ===
    public async Task<ApiResponse<BuildListResponse>?> GetBuildsAsync(int page = 1, int pageSize = 20)
    {
        return await GetJsonWithRetryAsync<ApiResponse<BuildListResponse>>(
            $"/api/builds?page={page}&pageSize={pageSize}", nameof(GetBuildsAsync));
    }

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
    public async Task<ApiResponse<PipelineListResponse>?> GetPipelinesAsync(int page = 1, int pageSize = 20)
    {
        return await GetJsonWithRetryAsync<ApiResponse<PipelineListResponse>>(
            $"/api/pipelines?page={page}&pageSize={pageSize}", nameof(GetPipelinesAsync));
    }

    public async Task<ApiResponse<PipelineResponse>?> GetPipelineAsync(Guid id)
    {
        return await GetJsonWithRetryAsync<ApiResponse<PipelineResponse>>(
            $"/api/pipelines/{id}", nameof(GetPipelineAsync));
    }

    public async Task<ApiResponse<PipelineResponse>?> CreatePipelineAsync(CreatePipelineRequest request)
    {
        var resp = await _http.PostAsJsonAsync("/api/pipelines", request);
        return await resp.Content.ReadFromJsonAsync<ApiResponse<PipelineResponse>>();
    }

    public async Task<ApiResponse<PipelineResponse>?> UpdatePipelineAsync(Guid id, UpdatePipelineRequest request)
    {
        var resp = await _http.PutAsJsonAsync($"/api/pipelines/{id}", request);
        return await resp.Content.ReadFromJsonAsync<ApiResponse<PipelineResponse>>();
    }

    public async Task<ApiResponse<object>?> DeletePipelineAsync(Guid id)
    {
        var resp = await _http.DeleteAsync($"/api/pipelines/{id}");
        return await resp.Content.ReadFromJsonAsync<ApiResponse<object>>();
    }

    public async Task<ApiResponse<BuildJobResponse>?> TriggerPipelineAsync(Guid pipelineId)
    {
        var resp = await _http.PostAsync($"/api/pipelines/{pipelineId}/trigger", null);
        return await resp.Content.ReadFromJsonAsync<ApiResponse<BuildJobResponse>>();
    }

    public async Task<ApiResponse<BuildJobResponse>?> TriggerPipelineWithRequestAsync(Guid pipelineId, TriggerBuildRequest request)
    {
        var resp = await _http.PostAsJsonAsync($"/api/pipelines/{pipelineId}/trigger", request);
        return await resp.Content.ReadFromJsonAsync<ApiResponse<BuildJobResponse>>();
    }

    public async Task<ApiResponse<BuildJobResponse>?> TriggerAutoBuildAsync(Guid pipelineId, string triggeredBy = "auto")
    {
        var resp = await _http.PostAsJsonAsync($"/api/pipelines/{pipelineId}/trigger-auto", new TriggerAutoBuildRequest(triggeredBy));
        return await resp.Content.ReadFromJsonAsync<ApiResponse<BuildJobResponse>>();
    }

    // === Security ===
    public async Task<ApiResponse<HashListResponse>?> GetHashRecordsAsync(int page = 1)
    {
        return await _http.GetFromJsonAsync<ApiResponse<HashListResponse>>($"/api/security/hashes?page={page}");
    }

    public async Task<ApiResponse<object>?> SignPackageAsync(SignPackageRequest request)
    {
        var resp = await _http.PostAsJsonAsync("/api/security/sign", request);
        return await resp.Content.ReadFromJsonAsync<ApiResponse<object>>();
    }

    public async Task<ApiResponse<object>?> GenerateKeyAsync(string keyName, string email, string passphrase)
    {
        var resp = await _http.PostAsJsonAsync("/api/security/keys/generate", new GenerateKeyRequest(keyName, email, passphrase));
        return await resp.Content.ReadFromJsonAsync<ApiResponse<object>>();
    }

    public async Task<ApiResponse<List<object>>?> GetKeysAsync()
    {
        return await _http.GetFromJsonAsync<ApiResponse<List<object>>>("/api/security/keys");
    }

    // === Scanner ===
    public async Task<ApiResponse<ScanListResponse>?> GetScansAsync(int page = 1)
    {
        return await _http.GetFromJsonAsync<ApiResponse<ScanListResponse>>($"/api/scanner/scans?page={page}");
    }

    public async Task<ApiResponse<CveReportResponse>?> GetScanReportAsync(Guid id)
    {
        return await _http.GetFromJsonAsync<ApiResponse<CveReportResponse>>($"/api/scanner/scans/{id}");
    }

    public async Task<ApiResponse<ScanResponse>?> StartScanAsync(ScanRequest request)
    {
        var resp = await _http.PostAsJsonAsync("/api/scanner/scan", request);
        return await resp.Content.ReadFromJsonAsync<ApiResponse<ScanResponse>>();
    }

    // === Repositories ===
    public async Task<ApiResponse<RepositoryListResponse>?> GetRepositoriesAsync()
    {
        return await _http.GetFromJsonAsync<ApiResponse<RepositoryListResponse>>("/api/repository");
    }

    public async Task<ApiResponse<RepositoryResponse>?> CreateRepositoryAsync(CreateRepositoryRequest request)
    {
        var resp = await _http.PostAsJsonAsync("/api/repository", request);
        return await resp.Content.ReadFromJsonAsync<ApiResponse<RepositoryResponse>>();
    }

    public async Task<ApiResponse<PackageResponse>?> UploadPackageAsync(Guid repositoryId, Stream fileStream, string fileName, string publishedBy = "upload")
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-rpm");
        content.Add(fileContent, "file", fileName);
        content.Add(new StringContent(repositoryId.ToString()), "repositoryId");
        content.Add(new StringContent(publishedBy), "publishedBy");

        var resp = await _http.PostAsync("/api/repository/upload", content);
        return await resp.Content.ReadFromJsonAsync<ApiResponse<PackageResponse>>();
    }

    public async Task<ApiResponse<object>?> SyncRepositoryAsync(Guid repositoryId)
    {
        var resp = await _http.PostAsJsonAsync("/api/repository/sync", new SyncRepositoryRequest(repositoryId));
        return await resp.Content.ReadFromJsonAsync<ApiResponse<object>>();
    }

    public async Task<ApiResponse<List<PackageResponse>>?> GetRepositoryPackagesAsync(Guid repositoryId)
    {
        return await _http.GetFromJsonAsync<ApiResponse<List<PackageResponse>>>($"/api/repository/{repositoryId}/packages");
    }

    public async Task<ApiResponse<PackageResponse>?> PublishPackageAsync(Guid artifactId, Guid repositoryId, string publishedBy = "build-service")
    {
        var resp = await _http.PostAsJsonAsync("/api/repository/publish", new PublishPackageRequest(artifactId, repositoryId, publishedBy));
        return await resp.Content.ReadFromJsonAsync<ApiResponse<PackageResponse>>();
    }

    // === Sources ===
    public async Task<ApiResponse<SourceListResponse>?> GetSourcesAsync()
    {
        return await _http.GetFromJsonAsync<ApiResponse<SourceListResponse>>("/api/sources");
    }

    public async Task<ApiResponse<SourcePackageResponse>?> GetSourceAsync(string name)
    {
        return await _http.GetFromJsonAsync<ApiResponse<SourcePackageResponse>>($"/api/sources/{name}");
    }

    public async Task<ApiResponse<SourceFetchResponse>?> FetchSourceAsync(string name)
    {
        var resp = await _http.PostAsync($"/api/sources/{name}/fetch", null);
        return await resp.Content.ReadFromJsonAsync<ApiResponse<SourceFetchResponse>>();
    }

    public async Task<string?> GetConfigAsync()
    {
        var resp = await _http.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/sources/config");
        return resp.TryGetProperty("content", out var c) ? c.GetString() : null;
    }

    public async Task<bool> SaveConfigAsync(string content)
    {
        var resp = await _http.PutAsJsonAsync("/api/sources/config", new { content });
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> RemovePackageFromConfigAsync(string name)
    {
        var resp = await _http.DeleteAsync($"/api/sources/config/{name}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<ApiResponse<object>?> AddPackageToConfigAsync(AddPackageToConfigRequest request)
    {
        var resp = await _http.PostAsJsonAsync("/api/sources/config/package", request);
        return await resp.Content.ReadFromJsonAsync<ApiResponse<object>>();
    }

    public async Task<ApiResponse<object>?> BuildSourceAsync(string name, string? specContent = null)
    {
        var body = specContent != null ? new { SpecContent = specContent } : (object?)null;
        var resp = await _http.PostAsJsonAsync($"/api/sources/{name}/build", body);
        if (resp.IsSuccessStatusCode)
        {
            return await resp.Content.ReadFromJsonAsync<ApiResponse<object>>();
        }
        var errorContent = await resp.Content.ReadAsStringAsync();
        _logger.LogWarning("Build source failed for {Name}: {Status} {Error}", name, resp.StatusCode, errorContent);
        return new ApiResponse<object>(false, null, $"Build failed ({resp.StatusCode}): {errorContent}", null);
    }

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

        var resp = await _http.PostAsync($"/api/extra-sources/pipeline/{pipelineId}", content);
        return await resp.Content.ReadFromJsonAsync<ApiResponse<List<UploadedSourceResponse>>>();
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
    {
        var resp = await _http.DeleteAsync($"/api/extra-sources/pipeline/{pipelineId}/{filePath}");
        return await resp.Content.ReadFromJsonAsync<ApiResponse<object>>();
    }

    public async Task<ApiResponse<object>?> ClearPipelineSourcesAsync(Guid pipelineId)
    {
        var resp = await _http.DeleteAsync($"/api/extra-sources/pipeline/{pipelineId}");
        return await resp.Content.ReadFromJsonAsync<ApiResponse<object>>();
    }
}