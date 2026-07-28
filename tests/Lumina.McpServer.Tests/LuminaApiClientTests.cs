using System.Net;
using System.Text;
using Lumina.McpServer;
using Xunit;

namespace Lumina.McpServer.Tests;

public sealed class LuminaApiClientTests
{
    [Fact]
    public async Task GetData_LogsInBeforeCallingApi()
    {
        var handler = new QueueHandler(
            Json(HttpStatusCode.OK, "{}"),
            Json(HttpStatusCode.OK, """{"success":true,"data":{"totalCount":2}}"""));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://lumina.example/")
        };
        using var client = new LuminaApiClient(
            http,
            new LuminaOptions(
                new Uri("https://lumina.example"),
                "agent",
                "secret",
                false));

        var data = await client.GetDataAsync("api/sources");

        Assert.Equal(2, data["totalCount"]!.GetValue<int>());
        Assert.Equal(
            new[] { "/api/auth/login", "/api/sources" },
            handler.RequestPaths);
    }

    [Fact]
    public async Task GetData_ReauthenticatesOnceAfterUnauthorized()
    {
        var handler = new QueueHandler(
            Json(HttpStatusCode.OK, "{}"),
            Json(HttpStatusCode.Unauthorized, """{"error":"expired"}"""),
            Json(HttpStatusCode.OK, "{}"),
            Json(HttpStatusCode.OK, """{"success":true,"data":{"status":2}}"""));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://lumina.example/")
        };
        using var client = new LuminaApiClient(
            http,
            new LuminaOptions(
                new Uri("https://lumina.example"),
                "agent",
                "secret",
                false));

        var data = await client.GetDataAsync("api/builds/build-id");

        Assert.Equal(2, data["status"]!.GetValue<int>());
        Assert.Equal(2, handler.RequestPaths.Count(
            path => path == "/api/auth/login"));
        Assert.Equal(2, handler.RequestPaths.Count(
            path => path == "/api/builds/build-id"));
    }

    private static HttpResponseMessage Json(
        HttpStatusCode statusCode,
        string body)
        => new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class QueueHandler(
        params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
