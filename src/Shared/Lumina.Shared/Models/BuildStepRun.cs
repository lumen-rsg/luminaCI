using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Models;

/// <summary>
/// Immutable snapshot of a pipeline-definition step for one build execution.
/// Runtime state belongs here rather than on <see cref="PipelineStep"/>, which
/// may be edited while older builds are still running.
/// </summary>
public class BuildStepRun
{
    public Guid Id { get; set; }
    public Guid BuildJobId { get; set; }
    public Guid PipelineStepId { get; set; }
    public int Order { get; set; }
    public StepType Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Configuration { get; set; } = new();
    public StepStatus Status { get; set; } = StepStatus.Pending;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }

    public BuildJob? BuildJob { get; set; }
}
