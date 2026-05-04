using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Models;

public class Pipeline
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public PipelineStatus Status { get; set; } = PipelineStatus.Draft;
    public List<PipelineStep> Steps { get; set; } = [];
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<string> Tags { get; set; } = [];

    // Git integration
    public string? GitRepoUrl { get; set; }
    public string? GitBranch { get; set; }
    public string? SpecPath { get; set; }  // Path to .spec file in repo, e.g. "pkg/my-package.spec"
    public string? WebhookSecret { get; set; }

    // Build configuration
    public string? BuildImage { get; set; }  // Docker image for builds, e.g. "lumina-rpm-build:latest" or "lumina-dotnet-build:latest"
}
