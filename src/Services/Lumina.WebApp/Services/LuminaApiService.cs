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

    public async Task<ApiResponse<object>?> CancelBuildAsync(Guid buildId)
    {
        var response = await _http.PostAsync($"/api/builds/{buildId}/cancel", null);
        return await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
    }

    public async Task<ApiResponse<object>?> ClearBuildQueueAsync()
    {
        var response = await _http.DeleteAsync("/api/builds/queue/clear");
        return await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
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