namespace Lumina.Shared.Models.Enums;

public enum ProjectWebhookStatus
{
    SnapshotPending = 0,
    SnapshotReady = 1,
    Dispatched = 2,
    Ignored = 3,
    Failed = 4,
    PlanReady = 5,
    Completed = 6
}
