using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lumina.McpServer;

public sealed class LuminaApiClient : ILuminaApiClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly LuminaOptions _options;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private bool _authenticated;

    public LuminaApiClient(HttpClient httpClient, LuminaOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public static LuminaApiClient Create(LuminaOptions options)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true
        };

        if (options.AllowInvalidTls)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        var client = new HttpClient(handler)
        {
            BaseAddress = EnsureTrailingSlash(options.BaseUri),
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Lumina-MCP/1.0");
        return new LuminaApiClient(client, options);
    }

    public Task<JsonNode> GetDataAsync(
        string path,
        CancellationToken cancellationToken = default)
        => SendDataAsync(HttpMethod.Get, path, null, cancellationToken);

    public Task<JsonNode> PostDataAsync(
        string path,
        object? body,
        CancellationToken cancellationToken = default)
        => SendDataAsync(HttpMethod.Post, path, body, cancellationToken);

    private async Task<JsonNode> SendDataAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        var response = await SendAsync(method, path, body, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            _authenticated = false;
            await EnsureAuthenticatedAsync(cancellationToken);
            response = await SendAsync(method, path, body, cancellationToken);
        }

        using (response)
        {
            var payload = await ReadJsonAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    ApiError(payload) ??
                    $"Lumina API returned HTTP {(int)response.StatusCode}.");
            }

            if (payload is not JsonObject envelope ||
                envelope["success"]?.GetValue<bool>() != true ||
                envelope["data"] is not JsonNode data)
            {
                throw new InvalidOperationException(
                    ApiError(payload) ?? "Lumina API returned an invalid response.");
            }

            return data.DeepClone();
        }
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (_authenticated)
            return;

        await _loginLock.WaitAsync(cancellationToken);
        try
        {
            if (_authenticated)
                return;

            using var response = await _httpClient.PostAsJsonAsync(
                "api/auth/login",
                new { _options.Username, _options.Password },
                JsonOptions,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var payload = await ReadJsonAsync(response, cancellationToken);
                throw new InvalidOperationException(
                    ApiError(payload) ??
                    $"Lumina login failed with HTTP {(int)response.StatusCode}.");
            }

            _authenticated = true;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        return _httpClient.SendAsync(request, cancellationToken);
    }

    private static async Task<JsonNode?> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<JsonNode>(
                JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ApiError(JsonNode? payload)
    {
        if (payload is not JsonObject obj)
            return null;

        return StringValue(obj["error"]) ?? StringValue(obj["message"]);
    }

    private static string? StringValue(JsonNode? node)
        => node is JsonValue value &&
           value.TryGetValue<string>(out var text) &&
           !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        var builder = new UriBuilder(uri);
        if (!builder.Path.EndsWith('/'))
            builder.Path += "/";
        return builder.Uri;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _loginLock.Dispose();
    }
}
