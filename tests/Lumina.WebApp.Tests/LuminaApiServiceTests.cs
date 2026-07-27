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
