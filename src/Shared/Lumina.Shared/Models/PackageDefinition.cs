namespace Lumina.Shared.Models;

/// <summary>
/// Stable identity for a source package. Source settings live in immutable
/// <see cref="PackageRevision"/> rows so running jobs never observe edits.
/// </summary>
public class PackageDefinition
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int ActiveRevisionNumber { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<PackageRevision> Revisions { get; set; } = [];
}
