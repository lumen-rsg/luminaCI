using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Lumina.Web.Shared.Auditing;

public sealed class AuditRequestMiddleware(RequestDelegate next)
{
    public const string AuthenticatedActorItemKey =
        "Lumina.Audit.AuthenticatedActor";

    private static readonly HashSet<string> MutationMethods =
        new(StringComparer.OrdinalIgnoreCase)
        {
            HttpMethods.Post,
            HttpMethods.Put,
            HttpMethods.Patch,
            HttpMethods.Delete
        };

    public async Task InvokeAsync(
        HttpContext context,
        IAuditLedgerWriter ledger)
    {
        if (!MutationMethods.Contains(context.Request.Method) ||
            !context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        var classified = Classify(context.Request.Path);
        var actor = context.User.Identity?.IsAuthenticated == true
            ? context.User.Identity.Name ??
              context.User.FindFirstValue(ClaimTypes.NameIdentifier) ??
              "authenticated"
            : "anonymous";
        var ipAddress = context.Connection.RemoteIpAddress?.ToString();
        var correlationId = context.TraceIdentifier;
        var action = $"http.{context.Request.Method.ToLowerInvariant()}";

        await ledger.AppendAsync(new AuditWriteRequest(
            action,
            classified.EntityType,
            classified.EntityId,
            actor,
            SerializeDetails(context, "Requested", null),
            ipAddress,
            correlationId,
            "Requested",
            null), context.RequestAborted);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
            stopwatch.Stop();
            var completedActor =
                context.Items.TryGetValue(
                    AuthenticatedActorItemKey, out var authenticatedActor) &&
                authenticatedActor is string trustedActor &&
                !string.IsNullOrWhiteSpace(trustedActor)
                    ? trustedActor
                    : actor;
            await ledger.AppendAsync(new AuditWriteRequest(
                action,
                classified.EntityType,
                classified.EntityId,
                completedActor,
                SerializeDetails(context, "Completed", stopwatch.ElapsedMilliseconds),
                ipAddress,
                correlationId,
                "Completed",
                context.Response.StatusCode), CancellationToken.None);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            await ledger.AppendAsync(new AuditWriteRequest(
                action,
                classified.EntityType,
                classified.EntityId,
                actor,
                JsonSerializer.Serialize(new
                {
                    method = context.Request.Method,
                    path = context.Request.Path.Value,
                    phase = "Failed",
                    durationMs = stopwatch.ElapsedMilliseconds,
                    errorType = exception.GetType().Name
                }),
                ipAddress,
                correlationId,
                "Failed",
                StatusCodes.Status500InternalServerError), CancellationToken.None);
            throw;
        }
    }

    private static string SerializeDetails(
        HttpContext context,
        string phase,
        long? durationMilliseconds)
        => JsonSerializer.Serialize(new
        {
            method = context.Request.Method,
            path = context.Request.Path.Value,
            endpoint = context.GetEndpoint()?.DisplayName,
            phase,
            durationMs = durationMilliseconds
        });

    private static (string EntityType, string EntityId) Classify(PathString path)
    {
        var segments = path.Value?
            .Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var entityType = segments.Length > 1 ? segments[1] : "api";
        var entityId = segments.Length > 2 ? segments[2] : string.Empty;
        return (entityType, entityId);
    }
}

public static class AuditRequestMiddlewareExtensions
{
    public static IApplicationBuilder UseLuminaAuditLedger(
        this IApplicationBuilder application)
        => application.UseMiddleware<AuditRequestMiddleware>();
}
