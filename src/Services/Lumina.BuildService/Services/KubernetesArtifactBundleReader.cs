using System.Formats.Tar;
using Lumina.Shared.Errors;

namespace Lumina.BuildService.Services;

internal sealed record KubernetesArtifactBundle(
    byte[] ManifestBytes,
    IReadOnlyDictionary<string, string> ArtifactPaths);

/// <summary>
/// Extracts the one exact output bundle uploaded by a build runner. The archive
/// has a deliberately tiny grammar and never trusts tar paths or link entries.
/// </summary>
internal static class KubernetesArtifactBundleReader
{
    public const string ManifestEntryName = "manifest.json";
    public const string ArtifactDirectoryName = "artifacts";
    public const int MaximumManifestBytes = 1024 * 1024;

    public static async Task<KubernetesArtifactBundle> ExtractAsync(
        Stream bundle,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("Destination directory is required.", nameof(destinationDirectory));
        Directory.CreateDirectory(destinationDirectory);

        byte[]? manifest = null;
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        long totalArtifactBytes = 0;
        using var reader = new TarReader(bundle, leaveOpen: true);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            if (entry.EntryType == TarEntryType.Directory &&
                string.Equals(entry.Name.TrimEnd('/'), ArtifactDirectoryName, StringComparison.Ordinal))
            {
                continue;
            }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                entry.DataStream == null)
            {
                throw new ValidationException("Kubernetes artifact bundle contains a forbidden tar entry.");
            }

            if (string.Equals(entry.Name, ManifestEntryName, StringComparison.Ordinal))
            {
                if (manifest != null || entry.Length is < 1 or > MaximumManifestBytes)
                    throw new ValidationException("Kubernetes artifact bundle manifest is missing or duplicated.");
                using var buffer = new MemoryStream((int)entry.Length);
                var copied = await KubernetesArtifactObjectStore.CopyBoundedAsync(
                    entry.DataStream,
                    buffer,
                    MaximumManifestBytes,
                    cancellationToken);
                if (copied != entry.Length)
                    throw new ValidationException("Kubernetes artifact bundle manifest size is invalid.");
                manifest = buffer.ToArray();
                continue;
            }

            var prefix = ArtifactDirectoryName + "/";
            if (!entry.Name.StartsWith(prefix, StringComparison.Ordinal))
                throw new ValidationException("Kubernetes artifact bundle contains an unknown entry.");
            var fileName = entry.Name[prefix.Length..];
            if (!KubernetesArtifactManifestPolicy.IsSafeRpmFileName(fileName) ||
                artifacts.Count >= KubernetesArtifactManifestPolicy.MaximumArtifacts ||
                entry.Length is < 1 or > KubernetesArtifactManifestPolicy.MaximumArtifactBytes)
            {
                throw new ValidationException("Kubernetes artifact bundle contains an invalid RPM entry.");
            }
            try
            {
                totalArtifactBytes = checked(totalArtifactBytes + entry.Length);
            }
            catch (OverflowException exception)
            {
                throw new ValidationException("Kubernetes artifact bundle size overflowed.", exception);
            }
            if (totalArtifactBytes > KubernetesArtifactManifestPolicy.MaximumTotalBytes)
                throw new ValidationException("Kubernetes artifact bundle exceeds the total artifact size limit.");

            var path = Path.Combine(destinationDirectory, fileName);
            if (!artifacts.TryAdd(fileName, path))
                throw new ValidationException("Kubernetes artifact bundle contains duplicate RPM filenames.");
            await using var destination = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var artifactBytes = await KubernetesArtifactObjectStore.CopyBoundedAsync(
                entry.DataStream,
                destination,
                entry.Length,
                cancellationToken);
            if (artifactBytes != entry.Length)
                throw new ValidationException("Kubernetes artifact bundle RPM size is invalid.");
        }

        if (manifest == null || artifacts.Count == 0)
            throw new ValidationException("Kubernetes artifact bundle is incomplete.");
        return new KubernetesArtifactBundle(manifest, artifacts);
    }

}
