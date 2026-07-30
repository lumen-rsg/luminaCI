using System.Net;
using Lumina.Shared.Models;
using Lumina.Web.Shared.Auditing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lumina.Shared.Tests;

public sealed class AuditRequestMiddlewareTests
{
    [Fact]
    public async Task MutationProducesRequestedAndCompletedEntries()
    {
        var ledger = new RecordingLedger();
        await using var application = await StartApplicationAsync(ledger);
        var client = application.GetTestClient();

        using var response = await client.PostAsync(
            "/api/pipelines/pipeline-1/trigger?secret=must-not-appear",
            new StringContent("""{"password":"must-not-appear"}"""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Collection(ledger.Requests,
            requested =>
            {
                Assert.Equal("Requested", requested.Phase);
                Assert.Equal("pipelines", requested.EntityType);
                Assert.Equal("pipeline-1", requested.EntityId);
                Assert.DoesNotContain("secret", requested.Details);
                Assert.DoesNotContain("password", requested.Details);
            },
            completed =>
            {
                Assert.Equal("Completed", completed.Phase);
                Assert.Equal(202, completed.StatusCode);
                Assert.Equal(
                    ledger.Requests[0].CorrelationId,
                    completed.CorrelationId);
            });
    }

    [Fact]
    public async Task ReadOnlyRequestIsNotAudited()
    {
        var ledger = new RecordingLedger();
        await using var application = await StartApplicationAsync(ledger);

        using var response = await application.GetTestClient()
            .GetAsync("/api/pipelines");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(ledger.Requests);
    }

    [Fact]
    public async Task TrustedEndpointActorIsUsedOnlyForCompletedEntry()
    {
        var ledger = new RecordingLedger();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IAuditLedgerWriter>(ledger);
        await using var application = builder.Build();
        application.UseLuminaAuditLedger();
        application.MapPost("/api/auth/login", (HttpContext context) =>
        {
            context.Items[AuditRequestMiddleware.AuthenticatedActorItemKey] =
                "verified-user";
            return Results.Ok();
        });
        await application.StartAsync();

        using var response = await application.GetTestClient()
            .PostAsync("/api/auth/login", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("anonymous", ledger.Requests[0].PerformedBy);
        Assert.Equal("verified-user", ledger.Requests[1].PerformedBy);
    }

    private static async Task<WebApplication> StartApplicationAsync(
        RecordingLedger ledger)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IAuditLedgerWriter>(ledger);

        var application = builder.Build();
        application.UseLuminaAuditLedger();
        application.MapPost(
            "/api/pipelines/{id}/trigger",
            () => Results.Accepted());
        application.MapGet("/api/pipelines", () => Results.Ok());
        await application.StartAsync();
        return application;
    }

    private sealed class RecordingLedger : IAuditLedgerWriter
    {
        public List<AuditWriteRequest> Requests { get; } = [];

        public Task<AuditLog> AppendAsync(
            AuditWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new AuditLog());
        }
    }
}
