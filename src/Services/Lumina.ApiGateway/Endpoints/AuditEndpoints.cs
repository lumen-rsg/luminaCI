using Lumina.ApiGateway.Data;
using Lumina.Shared.Auditing;
using Lumina.Shared.DTOs;
using Lumina.Web.Shared.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Lumina.ApiGateway.Endpoints;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/audit")
            .RequireAuthorization(AuthPolicies.Admin);

        group.MapGet("", async Task<IResult> (
            AuthDbContext database,
            int page = 1,
            int pageSize = 50,
            string? actor = null,
            string? entityType = null,
            string? correlationId = null,
            CancellationToken cancellationToken = default) =>
        {
            page = Math.Clamp(page, 1, 10_000_000);
            pageSize = Math.Clamp(pageSize, 1, 200);
            if (actor?.Length > 120 ||
                entityType?.Length > 120 ||
                correlationId?.Length > 128)
            {
                return Results.BadRequest(new
                {
                    error = "Audit filter exceeds its maximum length."
                });
            }

            var query = database.AuditLogs.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(actor))
            {
                query = query.Where(entry => entry.PerformedBy == actor);
            }
            if (!string.IsNullOrWhiteSpace(entityType))
            {
                query = query.Where(entry => entry.EntityType == entityType);
            }
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                query = query.Where(entry => entry.CorrelationId == correlationId);
            }

            var count = await query.CountAsync(cancellationToken);
            var entries = await query
                .OrderByDescending(entry => entry.Sequence)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(entry => new AuditLogResponse(
                    entry.Id,
                    entry.Sequence,
                    entry.Action,
                    entry.EntityType,
                    entry.EntityId,
                    entry.PerformedBy,
                    entry.Timestamp,
                    entry.Details,
                    entry.IpAddress,
                    entry.CorrelationId,
                    entry.Phase,
                    entry.StatusCode,
                    entry.PreviousHash,
                    entry.EntryHash))
                .ToListAsync(cancellationToken);

            return Results.Ok(new AuditLogListResponse(
                entries, count, page, pageSize));
        });

        group.MapGet("/integrity", async (
            AuthDbContext database,
            CancellationToken cancellationToken) =>
        {
            var previousHash = AuditHashChain.GenesisHash;
            long entryCount = 0;
            await foreach (var entry in database.AuditLogs
                .AsNoTracking()
                .OrderBy(entry => entry.Sequence)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken))
            {
                entryCount++;
                if (!AuditHashChain.VerifyEntry(entry, previousHash))
                {
                    return Results.Ok(new AuditIntegrityResponse(
                        false, entryCount, entry.Sequence));
                }

                previousHash = entry.EntryHash;
            }

            return Results.Ok(new AuditIntegrityResponse(
                true, entryCount, null));
        });

        return endpoints;
    }
}
