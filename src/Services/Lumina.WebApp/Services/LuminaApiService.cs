using System.Net.Http.Json;
using Lumina.Shared.DTOs;

namespace Lumina.WebApp.Services;

public class LuminaApiService
{
    private readonly HttpClient _http;
    private readonly ILogger<LuminaApiService> _logger;

    public LuminaApiService(HttpClient http, ILogger<LuminaApiService> logger)
    {
        _http = http;
        _logger = logger;
    }

    // === Builds ===
    public async Task<ApiResponse<BuildListResponse>?> GetBuildsAsync(int page = 1, int pageSize = 20)
    {
        return await _http.GetFromJsonAsync<ApiResponse<BuildListResponse>>($"/api/builds?page={page}&pageSize={pageSize}");
    }

    public async Task<ApiResponse<BuildQueueResponse>?> GetBuildQueueAsync()
    {
        return await _http.GetFromJsonAsync<ApiResponse<BuildQueueResponse>>("/api/builds/queue");
    }

    public async Task<ApiResponse<BuildJobResponse>?> GetBuildAsync(Guid id)
    {
        return await _http.GetFromJsonAsync<ApiResponse<BuildJobResponse>>($"/api/builds/{id}");
    }

    public async Task<ApiResponse<string>?> GetBuildLogsAsync(Guid id)
    {
        return await _http.GetFromJsonAsync<ApiResponse<string>>($"/api/builds/{id}/logs");
    }

    // === Pipelines ===
    public async Task<ApiResponse<PipelineListResponse>?> GetPipelinesAsync(int page = 1, int pageSize = 20)
    {
        return await _http.GetFromJsonAsync<ApiResponse<PipelineListResponse>>($"/api/pipelines?page={page}&pageSize={pageSize}");
    }

    public async Task<ApiResponse<PipelineResponse>?> GetPipelineAsync(Guid id)
    {
        return await _http.GetFromJsonAsync<ApiResponse<PipelineResponse>>($"/api/pipelines/{id}");
    }

    public async Task<ApiResponse<PipelineResponse>?> CreatePipelineAsync(CreatePipelineRequest request)
    {
        var resp = await _http.PostAsJsonAsync("/api/pipelines", request);
        return await resp.Content.ReadFromJsonAsync<ApiResponse<PipelineResponse>>();
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

    // === Auth ===
    public async Task<string?> LoginAsync(string username, string password)
    {
        var resp = await _http.PostAsJsonAsync("/api/auth/login", new { username, password });
        if (resp.IsSuccessStatusCode)
        {
            var result = await resp.Content.ReadFromJsonAsync<LoginResult>();
            return result?.Token;
        }
        return null;
    }

    private record LoginResult(string Token, DateTime Expires);
}