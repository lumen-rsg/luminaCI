using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Events;

// Build Events
public record BuildJobCreated(Guid BuildJobId, Guid PipelineId, string SpecName, string TriggeredBy, DateTime CreatedAt);

public record BuildJobStarted(Guid BuildJobId, string ContainerId, DateTime StartedAt);

public record BuildJobCompleted(Guid BuildJobId, BuildStatus Status, DateTime CompletedAt);

public record BuildJobFailed(Guid BuildJobId, string ErrorMessage, DateTime FailedAt);