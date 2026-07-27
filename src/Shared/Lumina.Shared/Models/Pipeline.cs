using System.Text.Json.Serialization;
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

    // WebhookSecret is stored encrypted at rest (see AesSecretProtector) and is
    // never serialized over the wire — the PipelineResponse DTO exposes only a
    // boolean presence flag (HasWebhookSecret). [JsonIgnore] is defense-in-depth:
    // any code path that serializes this entity directly still omits the secret.
    [JsonIgnore]
    public string? WebhookSecret { get; set; }

    // Git credentials (for private repositories). GitUsername is non-secret
    // (it's surfaced via PipelineResponse); GitToken is sensitive, so it is
    // encrypted at rest and excluded from serialization entirely.
    public string? GitUsername { get; set; }

    [JsonIgnore]
    public string? GitToken { get; set; }  // Personal Access Token or deploy key password

    // Build configuration
    public string? BuildImage { get; set; }
    public string TargetDistribution { get; set; } = "fedora";
    public string TargetRelease { get; set; } = "44";
    public string TargetArchitecture { get; set; } = "aarch64";
    public string BuildProfile { get; set; } = "fedora-44-aarch64";

    // Spec content (for packages where .spec is NOT in the git repo)
    public string? SpecContent { get; set; }  // Full .spec file content — used by Auto Build when repo has no spec
}
