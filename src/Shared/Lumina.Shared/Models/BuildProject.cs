using System.Text.Json.Serialization;

namespace Lumina.Shared.Models;

/// <summary>
/// Stable identity for a source repository whose package graph is described by
/// a repository-owned manifest. Secrets are encrypted by BuildDbContext and
/// never serialized from this entity.
/// </summary>
public class BuildProject
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string GitRepoUrl { get; set; } = string.Empty;
    public string GitBranch { get; set; } = "main";
    public string ManifestPath { get; set; } = ".lumina/packages.yaml";
    public bool IsActive { get; set; } = true;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string WebhookSecret { get; set; } = string.Empty;

    public string? GitUsername { get; set; }

    [JsonIgnore]
    public string? GitToken { get; set; }

    public List<Pipeline> Pipelines { get; set; } = [];
}
