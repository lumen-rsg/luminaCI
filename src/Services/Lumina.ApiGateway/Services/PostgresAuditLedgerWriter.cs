using System.Data;
using Lumina.ApiGateway.Data;
using Lumina.Shared.Auditing;
using Lumina.Shared.Models;
using Lumina.Web.Shared.Auditing;
using Microsoft.EntityFrameworkCore;

namespace Lumina.ApiGateway.Services;

public sealed class PostgresAuditLedgerWriter(AuthDbContext database)
    : IAuditLedgerWriter
{
    public async Task<AuditLog> AppendAsync(
        AuditWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        await database.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(4812736491023)",
            cancellationToken);

        var previousHash = await database.AuditLogs
            .OrderByDescending(entry => entry.Sequence)
            .Select(entry => entry.EntryHash)
            .FirstOrDefaultAsync(cancellationToken) ?? AuditHashChain.GenesisHash;

        var entry = new AuditLog
        {
            Id = Guid.CreateVersion7(),
            Action = request.Action,
            EntityType = request.EntityType,
            EntityId = request.EntityId,
            PerformedBy = request.PerformedBy,
            Timestamp = DateTimeOffset.UtcNow,
            Details = request.Details,
            IpAddress = request.IpAddress,
            CorrelationId = request.CorrelationId,
            Phase = request.Phase,
            StatusCode = request.StatusCode,
            PreviousHash = previousHash
        };
        entry.EntryHash = AuditHashChain.Compute(entry);

        database.AuditLogs.Add(entry);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return entry;
    }

    private static void Validate(AuditWriteRequest request)
    {
        Require(request.Action, nameof(request.Action), 120);
        Require(request.EntityType, nameof(request.EntityType), 120);
        Require(request.PerformedBy, nameof(request.PerformedBy), 120);
        Require(request.CorrelationId, nameof(request.CorrelationId), 128);
        Require(request.Phase, nameof(request.Phase), 32);
        Require(request.EntityId, nameof(request.EntityId), 240, allowEmpty: true);
        Require(request.Details, nameof(request.Details), 16_384);
        if (request.IpAddress?.Length > 64)
        {
            throw new ArgumentException("IP address is too long.", nameof(request));
        }
    }

    private static void Require(
        string value,
        string field,
        int maximumLength,
        bool allowEmpty = false)
    {
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) ||
            value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"{field} must contain at most {maximumLength} characters.",
                field);
        }
    }
}
