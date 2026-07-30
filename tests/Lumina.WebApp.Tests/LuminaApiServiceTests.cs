using System.Net;
using System.Text;
using Lumina.WebApp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumina.WebApp.Tests;

public class LuminaApiServiceTests
{
    [Fact]
    public async Task Non_success_envelope_throws_one_typed_exception()
    {
        var handler = new StubHandler(_ => Json(
            HttpStatusCode.BadRequest,
            """{"success":false,"error":"pipeline is invalid"}"""));
        var api = CreateApi(handler);

        var exception = await Assert.ThrowsAsync<ApiRequestException>(
            () => api.GetPipelinesAsync());

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("pipeline is invalid", exception.Message);
    }

    [Fact]
    public async Task Safe_get_retries_transient_responses()
    {
        var attempt = 0;
        var handler = new StubHandler(_ =>
        {
            attempt++;
            return attempt < 3
                ? Json(HttpStatusCode.ServiceUnavailable, """{"error":"warming up"}""")
                : Json(HttpStatusCode.OK,
                    """{"success":true,"data":{"pipelines":[],"totalCount":0,"page":1,"pageSize":20}}""");
        });
        var api = CreateApi(handler);

        var response = await api.GetPipelinesAsync();

        Assert.True(response!.Success);
        Assert.Equal(3, attempt);
    }

    [Fact]
    public async Task Empty_success_body_is_not_returned_as_null()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("", Encoding.UTF8, "application/json")
        });
        var api = CreateApi(handler);

        await Assert.ThrowsAsync<ApiRequestException>(() => api.GetBuildStatsAsync());
    }

    [Fact]
    public async Task Repositories_deserialize_the_list_response_contract()
    {
        var repositoryId = Guid.NewGuid();
        var handler = new StubHandler(request =>
        {
            Assert.Equal("/api/repository", request.RequestUri!.AbsolutePath);
            return Json(
                HttpStatusCode.OK,
                $$"""
                {
                  "success": true,
                  "data": {
                    "repositories": [{
                      "id": "{{repositoryId}}",
                      "name": "stable",
                      "displayName": "Stable",
                      "basePath": "stable",
                      "arch": "aarch64",
                      "distribution": "fedora",
                      "isActive": true,
                      "createdAt": "2026-07-30T00:00:00Z",
                      "packageCount": 3
                    }],
                    "totalCount": 1,
                    "page": 1,
                    "pageSize": 1
                  },
                  "error": null,
                  "message": null
                }
                """);
        });
        var api = CreateApi(handler);

        var response = await api.GetRepositoriesAsync();

        var repository = Assert.Single(response!.Data!.Repositories);
        Assert.Equal(repositoryId, repository.Id);
        Assert.Equal(3, repository.PackageCount);
        Assert.Equal(1, response.Data.TotalCount);
    }

    private static LuminaApiService CreateApi(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://lumina.test") },
            NullLogger<LuminaApiService>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
