using Lumina.Shared.Models;

namespace Lumina.Web.Shared.Auditing;

public sealed record AuditWriteRequest(
    string Action,
    string EntityType,
    string EntityId,
    string PerformedBy,
    string Details,
    string? IpAddress,
    string CorrelationId,
    string Phase,
    int? StatusCode);

public interface IAuditLedgerWriter
{
    Task<AuditLog> AppendAsync(
        AuditWriteRequest request,
        CancellationToken cancellationToken = default);
}
