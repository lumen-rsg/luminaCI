namespace Lumina.Shared.Models;

public class PackageRepository
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string BasePath { get; set; } = string.Empty;
    public string Arch { get; set; } = "x86_64";
    public string Distribution { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public List<Package> Packages { get; set; } = [];
}