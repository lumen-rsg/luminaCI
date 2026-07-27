using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Models;

public class BuildJob
{
    public Guid Id { get; set; }
    public Guid PipelineId { get; set; }
    public BuildStatus Status { get; set; } = BuildStatus.Queued;
    public string SpecName { get; set; } = string.Empty;
    public string SpecContent { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string? ContainerId { get; set; }
    public string Logs { get; set; } = string.Empty;
    public List<BuildArtifact> Artifacts { get; set; } = [];
    public List<BuildStepRun> StepRuns { get; set; } = [];
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string TriggeredBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Git integration — commit/branch info from webhook
    public string? CommitSha { get; set; }
    public string? Branch { get; set; }
    public string? CommitMessage { get; set; }
    public string? CommitAuthor { get; set; }

    // Immutable build target and runner snapshot captured when the job starts.
    public string TargetDistribution { get; set; } = string.Empty;
    public string TargetRelease { get; set; } = string.Empty;
    public string TargetArchitecture { get; set; } = string.Empty;
    public string BuildProfile { get; set; } = string.Empty;
    public string? RunnerImageReference { get; set; }
    public string? RunnerImageDigest { get; set; }

    public Pipeline? Pipeline { get; set; }
}
