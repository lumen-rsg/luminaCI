using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;

namespace Lumina.RepositoryService.Services;

public sealed record RepositoryManifestEntry(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("release")] string Release,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

public static class RepositoryManifestPolicy
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Compute(IEnumerable<Package> packages) =>
        Compute(packages.Select(FromPackage));

    public static string Compute(IEnumerable<RepositoryManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var normalized = entries.Select(Normalize)
            .OrderBy(item => item.Architecture, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Version, StringComparer.Ordinal)
            .ThenBy(item => item.Release, StringComparer.Ordinal)
            .ThenBy(item => item.FileName, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .ToList();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, Json);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static RepositoryManifestEntry FromPackage(Package package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new RepositoryManifestEntry(
            package.Id, package.Name, package.Version, package.Release, package.Arch,
            package.FileName, package.FileSize, package.HashSha256 ?? string.Empty);
    }

    private static RepositoryManifestEntry Normalize(RepositoryManifestEntry entry)
    {
        if (entry.Id == Guid.Empty || string.IsNullOrWhiteSpace(entry.Name) ||
            string.IsNullOrWhiteSpace(entry.Version) || string.IsNullOrWhiteSpace(entry.Release) ||
            string.IsNullOrWhiteSpace(entry.Architecture) || Path.GetFileName(entry.FileName) != entry.FileName ||
            entry.Size <= 0 || entry.Sha256 is not { Length: 64 } hash ||
            hash.Any(character => !Uri.IsHexDigit(character)))
            throw new ValidationException("Repository manifest entry is invalid.");
        return entry with { Sha256 = hash.ToLowerInvariant() };
    }
}
