using Lumina.Shared.Errors;

namespace Lumina.BuildService.Services.PackageGraph;

public static class ProjectLookasideSourcePolicy
{
    public const int MaximumSourcesPerPackage = 32;
    public const long MaximumSourceBytes = 1024L * 1024 * 1024;
    public const long MaximumPackageSourceBytes = 4L * 1024 * 1024 * 1024;

    public static IReadOnlyList<ProjectLookasideSource> Normalize(
        IReadOnlyList<RepositoryLookasideSource>? sources)
    {
        if (sources is null or { Count: 0 })
            return [];
        if (sources.Count > MaximumSourcesPerPackage)
            throw new ValidationException(
                $"A package may declare at most {MaximumSourcesPerPackage} lookaside sources.");
        var normalized = sources.Select(source =>
        {
            var fileName = source.FileName ?? string.Empty;
            var hash = (source.Sha256 ?? string.Empty).Trim().ToLowerInvariant();
            if (fileName.Length is 0 or > 256 ||
                fileName.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                                          character is not '.' and not '_' and not '+' and not '-') ||
                source.Size is <= 0 or > MaximumSourceBytes ||
                hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
                throw new ValidationException("Lookaside source identity is invalid.");
            return new ProjectLookasideSource(
                fileName,
                ObjectName(fileName, hash),
                source.Size,
                hash);
        }).OrderBy(source => source.FileName, StringComparer.Ordinal).ToList();
        if (normalized.Select(source => source.FileName).Distinct(StringComparer.Ordinal).Count() !=
            normalized.Count)
            throw new ValidationException("Lookaside source filenames must be unique per package.");
        if (normalized.Sum(source => source.Size) > MaximumPackageSourceBytes)
            throw new ValidationException("Lookaside sources exceed the per-package size policy.");
        return normalized;
    }

    public static void Validate(IReadOnlyList<ProjectLookasideSource>? sources)
    {
        if (sources is null or { Count: 0 })
            return;
        var normalized = Normalize(sources.Select(source => new RepositoryLookasideSource(
            source.FileName, source.Size, source.Sha256)).ToList());
        if (sources.Count != normalized.Count || sources.Zip(normalized).Any(pair =>
                pair.First.FileName != pair.Second.FileName ||
                pair.First.ObjectName != pair.Second.ObjectName ||
                pair.First.Size != pair.Second.Size ||
                pair.First.Sha256 != pair.Second.Sha256))
            throw new ValidationException("Persisted lookaside source identity changed.");
    }

    public static string ObjectName(string fileName, string sha256) =>
        $"lookaside/sha256/{sha256}/{fileName}";
}
