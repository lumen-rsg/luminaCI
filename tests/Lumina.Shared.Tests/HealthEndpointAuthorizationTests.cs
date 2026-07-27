using System.Net;
using System.Text.Encodings.Web;
using Lumina.Web.Shared;
using Lumina.Web.Shared.Health;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lumina.Shared.Tests;

public sealed class HealthEndpointAuthorizationTests
{
    [Fact]
    public async Task HealthEndpointsAreAnonymousWhileApplicationEndpointsRemainProtected()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, AnonymousTestHandler>("test", _ => { });
        builder.Services.AddLuminaAuthorization();
        builder.Services.AddHealthChecks()
            .AddCheck("live", () => HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck("ready", () => HealthCheckResult.Healthy(), tags: ["ready"]);

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/protected", () => Results.Ok());
        app.MapLuminaHealthChecks();

        await app.StartAsync();
        var client = app.GetTestClient();

        foreach (var path in new[] { "/health/startup", "/health/live", "/health/ready", "/health" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var protectedResponse = await client.GetAsync("/protected");
        Assert.Equal(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
    }

    private sealed class AnonymousTestHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.NoResult());
    }
}
