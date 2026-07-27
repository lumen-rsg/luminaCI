using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Models;

/// <summary>An immutable snapshot of all inputs needed to resolve a package source.</summary>
public class PackageRevision
{
    public Guid Id { get; set; }
    public Guid PackageDefinitionId { get; set; }
    public int RevisionNumber { get; set; }
    public SourceType SourceType { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public string? SourceReference { get; set; }
    public string? ExpectedSha256 { get; set; }
    public string? SpecPath { get; set; }
    public string? BuildImage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;

    public PackageDefinition PackageDefinition { get; set; } = null!;
    public List<SourceJob> SourceJobs { get; set; } = [];
}
