using System.Text;
using Lumina.Shared.Errors;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lumina.BuildService.Services.PackageGraph;

/// <summary>
/// Parses the bounded repository-owned <c>.lumina/packages.yaml</c> document.
/// Semantic and dependency validation is performed by <see cref="PackageGraphPlanner"/>.
/// </summary>
public static class PackageGraphManifestLoader
{
    public const int MaxManifestBytes = 256 * 1024;
    private const int MaxYamlEvents = 20_000;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .WithMaximumRecursion(32)
        .Build();

    public static RepositoryPackageGraph Load(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            throw new ValidationException("Package graph manifest must not be empty.");
        if (Encoding.UTF8.GetByteCount(yaml) > MaxManifestBytes)
        {
            throw new ValidationException(
                $"Package graph manifest exceeds the {MaxManifestBytes}-byte limit.");
        }

        ManifestDocument document;
        try
        {
            RejectAliasesAndExcessiveNodes(yaml);
            document = Deserializer.Deserialize<ManifestDocument>(yaml)
                ?? throw new ValidationException("Package graph manifest must contain a document.");
        }
        catch (ValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is YamlException or InvalidOperationException)
        {
            throw new ValidationException($"Package graph manifest is invalid YAML: {exception.Message}");
        }

        if (document.Packages is null)
            throw new ValidationException("Package graph manifest must declare a packages mapping.");

        var packages = document.Packages.Select(pair =>
        {
            var value = pair.Value ?? throw new ValidationException(
                $"Package '{pair.Key}' must contain a definition.");
            return new RepositoryPackageDefinition(
                pair.Key,
                value.Spec ?? string.Empty,
                value.Paths ?? [],
                value.Targets ?? [],
                value.DependsOn,
                value.PromotionGroup,
                value.RebuildOnDependencyChange,
                value.LookasideSources?.Select(source => new RepositoryLookasideSource(
                    source.File ?? string.Empty,
                    source.Size,
                    source.Sha256 ?? string.Empty)).ToList());
        }).ToList();

        return new RepositoryPackageGraph(document.Version, packages);
    }

    private static void RejectAliasesAndExcessiveNodes(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        var eventCount = 0;
        while (parser.MoveNext())
        {
            eventCount++;
            if (eventCount > MaxYamlEvents)
                throw new ValidationException("Package graph manifest contains too many YAML nodes.");
            if (parser.Current is AnchorAlias ||
                parser.Current is NodeEvent node && !node.Anchor.IsEmpty)
            {
                throw new ValidationException("Package graph manifest may not use YAML anchors or aliases.");
            }
        }
    }

    public static PackageBuildPlan LoadAndPlan(
        string yaml,
        IReadOnlyCollection<string> changedPaths,
        IReadOnlySet<string> supportedTargets) =>
        PackageGraphPlanner.Plan(Load(yaml), changedPaths, supportedTargets);

    private sealed class ManifestDocument
    {
        public int Version { get; init; }
        public Dictionary<string, PackageDocument?>? Packages { get; init; }
    }

    private sealed class PackageDocument
    {
        public string? Spec { get; init; }
        public List<string>? Paths { get; init; }
        public List<string>? Targets { get; init; }
        public List<string>? DependsOn { get; init; }
        public string? PromotionGroup { get; init; }
        public bool RebuildOnDependencyChange { get; init; } = true;
        public List<LookasideSourceDocument>? LookasideSources { get; init; }
    }

    private sealed class LookasideSourceDocument
    {
        public string? File { get; init; }
        public long Size { get; init; }
        public string? Sha256 { get; init; }
    }
}
