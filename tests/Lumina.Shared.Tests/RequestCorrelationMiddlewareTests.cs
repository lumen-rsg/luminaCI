using System.Net;
using System.Text.Json;
using Lumina.Web.Shared.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lumina.Shared.Tests;

public sealed class RequestCorrelationMiddlewareTests
{
    [Fact]
    public async Task GeneratesAndReturnsCorrelationIdentifier()
    {
        await using var application = await StartApplicationAsync();
        var client = application.GetTestClient();

        using var response = await client.GetAsync("/correlation");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseIdentifier = Assert.Single(
            response.Headers.GetValues(RequestCorrelationMiddleware.HeaderName));
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(responseIdentifier, payload.RootElement.GetProperty("traceIdentifier").GetString());
        Assert.Equal(responseIdentifier, payload.RootElement.GetProperty("requestHeader").GetString());
        Assert.Matches("^[a-f0-9]{32}$", responseIdentifier);
    }

    [Fact]
    public async Task PreservesSafeCallerCorrelationIdentifier()
    {
        await using var application = await StartApplicationAsync();
        var client = application.GetTestClient();
        const string expected = "release:fedora-44_build.123";

        using var request = new HttpRequestMessage(HttpMethod.Get, "/correlation");
        request.Headers.Add(RequestCorrelationMiddleware.HeaderName, expected);
        using var response = await client.SendAsync(request);

        Assert.Equal(expected, Assert.Single(
            response.Headers.GetValues(RequestCorrelationMiddleware.HeaderName)));
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, payload.RootElement.GetProperty("traceIdentifier").GetString());
        Assert.Equal(expected, payload.RootElement.GetProperty("requestHeader").GetString());
    }

    [Theory]
    [InlineData("contains a space")]
    [InlineData("contains/a/slash")]
    public async Task ReplacesUnsafeCallerCorrelationIdentifier(string supplied)
    {
        await using var application = await StartApplicationAsync();
        var client = application.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/correlation");
        request.Headers.TryAddWithoutValidation(RequestCorrelationMiddleware.HeaderName, supplied);
        using var response = await client.SendAsync(request);

        var actual = Assert.Single(
            response.Headers.GetValues(RequestCorrelationMiddleware.HeaderName));
        Assert.NotEqual(supplied, actual);
        Assert.Matches("^[a-f0-9]{32}$", actual);
    }

    private static async Task<WebApplication> StartApplicationAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var application = builder.Build();
        application.UseLuminaRequestCorrelation();
        application.MapGet("/correlation", (HttpContext context) => Results.Json(new
        {
            traceIdentifier = context.TraceIdentifier,
            requestHeader = context.Request.Headers[
                RequestCorrelationMiddleware.HeaderName].ToString()
        }));

        await application.StartAsync();
        return application;
    }
}
