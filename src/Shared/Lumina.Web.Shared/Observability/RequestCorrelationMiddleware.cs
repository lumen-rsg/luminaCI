using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Lumina.Web.Shared.Observability;

/// <summary>
/// Establishes one safe correlation identifier at the public request boundary
/// and keeps it stable as the request is proxied between Lumina services.
/// </summary>
public sealed class RequestCorrelationMiddleware(
    RequestDelegate next,
    ILogger<RequestCorrelationMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";
    private const int MaxIdentifierLength = 128;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);

        context.TraceIdentifier = correlationId;
        context.Request.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("lumina.correlation_id", correlationId);

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object>
               {
                   ["CorrelationId"] = correlationId
               }))
        {
            await next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out StringValues supplied) &&
            supplied.Count == 1 &&
            IsSafeIdentifier(supplied[0]))
        {
            return supplied[0]!;
        }

        var traceId = Activity.Current?.TraceId;
        return traceId is { } current && current != default
            ? current.ToString()
            : Guid.NewGuid().ToString("N");
    }

    private static bool IsSafeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdentifierLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.' and not ':')
            {
                return false;
            }
        }

        return true;
    }
}

public static class RequestCorrelationExtensions
{
    /// <summary>
    /// Adds correlation before authentication, controllers, and proxying so
    /// every request log and downstream HTTP call can share the same identifier.
    /// </summary>
    public static IApplicationBuilder UseLuminaRequestCorrelation(
        this IApplicationBuilder application)
        => application.UseMiddleware<RequestCorrelationMiddleware>();
}
