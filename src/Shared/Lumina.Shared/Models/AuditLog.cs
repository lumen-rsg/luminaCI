namespace Lumina.Shared.Models;

public class AuditLog
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string PerformedBy { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Details { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public string PreviousHash { get; set; } = string.Empty;
    public string EntryHash { get; set; } = string.Empty;
}
