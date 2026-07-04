using Lumina.Shared.DTOs;
using Lumina.Shared.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Lumina.Web.Shared.Errors;

/// <summary>
/// Translates thrown exceptions into structured <see cref="ApiResponse{T}"/>
/// action results. This is the single chokepoint that keeps raw exception text
/// off the wire (SEC-022): only the messages of <see cref="DomainException"/>
/// subtypes — which are authored by application code, never a driver or stack
/// frame — are returned to the client. Every other exception is logged in full
/// server-side and surfaced as a fixed generic message.
/// </summary>
public static class ApiResults
{
    /// <summary>
    /// The error string returned for any non-domain exception. Intentionally
    /// free of interpolated values so it cannot leak a path, SQL fragment, or
    /// stack hint regardless of what the underlying fault was.
    /// </summary>
    public const string GenericMessage = "An unexpected error occurred. Please try again later.";

    /// <summary>
    /// Maps <paramref name="ex"/> to a structured <see cref="ApiResponse{T}"/>
    /// result. <see cref="DomainException"/>s map to 4xx with their own
    /// message; everything else logs the full exception against
    /// <paramref name="logger"/> (tagged with <paramref name="operation"/> and
    /// the structured <paramref name="args"/>) and returns a generic 500.
    /// </summary>
    /// <typeparam name="T">The <see cref="ApiResponse{T}"/> payload type.</typeparam>
    /// <param name="ex">The exception thrown while serving the request.</param>
    /// <param name="logger">Caller's logger; receives the full exception.</param>
    /// <param name="operation">Short label identifying the failing operation (logged, not returned).</param>
    /// <param name="args">Optional structured parameters for the log message.</param>
    public static ObjectResult FromException<T>(
        Exception ex,
        ILogger logger,
        string operation,
        params object?[] args)
    {
        // Domain exceptions carry an application-authored message that is safe
        // to return. They represent expected control flow (not found, conflict,
        // validation), so they log as warnings, not errors.
        if (ex is DomainException dx)
        {
            logger.LogWarning(ex, "Domain error in {Operation}", operation);
            var domainPayload = new ApiResponse<T>(false, default, dx.Message, null);
            return dx switch
            {
                NotFoundException => new NotFoundObjectResult(domainPayload),
                ConflictException => new ConflictObjectResult(domainPayload),
                _ => new BadRequestObjectResult(domainPayload),
            };
        }

        // Everything else (DB driver fault, I/O, NullReferenceException, ...)
        // is logged in full server-side and surfaced to the caller as a fixed
        // generic message. ex.Message is NEVER interpolated into the response.
        logger.LogError(ex, "Unhandled exception in {Operation} {Args}", operation, args);
        var payload = new ApiResponse<T>(false, default, GenericMessage, null);
        return new ObjectResult(payload) { StatusCode = StatusCodes.Status500InternalServerError };
    }
}
