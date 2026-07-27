using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;

namespace Lumina.SourceService.Services;

/// <summary>
/// Enforces network and archive limits at the source trust boundary.
/// Connections use the exact addresses approved by <see cref="SourceUriValidator"/>,
/// redirects are revalidated one hop at a time, and archives are extracted
/// without permitting links, special files, traversal, or expansion bombs.
/// </summary>
public sealed class SourceIntegrityService
{
    private readonly SourceUriValidator _uriValidator;
    private readonly long _maxDownloadBytes;
    private readonly long _maxExpandedBytes;
    private readonly int _maxFileCount;
    private readonly int _maxCompressionRatio;
    private readonly int _maxRedirects;
    private readonly TimeSpan _fetchTimeout;

    public SourceIntegrityService(
        SourceUriValidator uriValidator,
        IConfiguration configuration)
    {
        _uriValidator = uriValidator;
        _maxDownloadBytes = Math.Clamp(
            configuration.GetValue("Source:MaxDownloadBytes", 1_073_741_824L),
            1_048_576L, 10_737_418_240L);
        _maxExpandedBytes = Math.Clamp(
            configuration.GetValue("Source:MaxExpandedBytes", 2_147_483_648L),
            1_048_576L, 21_474_836_480L);
        _maxFileCount = Math.Clamp(
            configuration.GetValue("Source:MaxFileCount", 100_000),
            1, 1_000_000);
        _maxCompressionRatio = Math.Clamp(
            configuration.GetValue("Source:MaxCompressionRatio", 100),
            1, 1_000);
        _maxRedirects = Math.Clamp(
            configuration.GetValue("Source:MaxRedirects", 5), 0, 10);
        _fetchTimeout = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("Source:FetchTimeoutMinutes", 30), 1, 240));
    }

    public async Task<string> DownloadHttpAsync(
        string sourceUrl,
        string targetPath,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_fetchTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        try
        {
            return await DownloadHttpCoreAsync(
                sourceUrl, targetPath, linked.Token);
        }
        catch (OperationCanceledException)
            when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Source download exceeded the {_fetchTimeout.TotalMinutes:g}-minute timeout.");
        }
    }

    private async Task<string> DownloadHttpCoreAsync(
        string sourceUrl,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var current = new Uri(sourceUrl, UriKind.Absolute);

        for (var redirect = 0; ; redirect++)
        {
            var validated = await _uriValidator.ValidateAsync(
                current.ToString(),
                Lumina.Shared.Models.Enums.SourceType.Http,
                branch: null,
                cancellationToken);

            using var handler = CreatePinnedHandler(validated.Addresses);
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (IsRedirect(response.StatusCode))
            {
                if (redirect >= _maxRedirects)
                    throw new SourceValidationException("Source exceeded the redirect limit.");
                if (response.Headers.Location is null)
                    throw new SourceValidationException("Source redirect omitted its destination.");

                current = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Source server returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > _maxDownloadBytes)
                throw new SourceValidationException("Source exceeds the maximum download size.");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81_920, useAsync: true);
            await CopyBoundedAsync(input, output, _maxDownloadBytes, cancellationToken);
            return RedactUri(current);
        }
    }

    public async Task ExtractTarAsync(
        string archivePath,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        var compressedBytes = new FileInfo(archivePath).Length;
        if (compressedBytes > _maxDownloadBytes)
            throw new SourceValidationException("Source exceeds the maximum download size.");

        Directory.CreateDirectory(targetDirectory);
        await using var archive = File.OpenRead(archivePath);
        await using var payload = OpenTarPayload(archivePath, archive);
        using var reader = new TarReader(payload, leaveOpen: false);
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        long expandedBytes = 0;
        var entryCount = 0;
        var root = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;

        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryCount++;
            if (entryCount > _maxFileCount)
                throw new SourceValidationException("Archive exceeds the maximum file count.");

            var destination = ResolveArchivePath(root, entry.Name);
            if (!seenPaths.Add(destination))
                throw new SourceValidationException("Archive contains duplicate paths.");

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(destination);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    expandedBytes = checked(expandedBytes + entry.Length);
                    AssertExpansionWithinLimits(expandedBytes, compressedBytes);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (entry.DataStream is null)
                        throw new InvalidDataException("Archive file entry has no data.");
                    await using (var output = new FileStream(
                                     destination, FileMode.CreateNew, FileAccess.Write,
                                     FileShare.None, 81_920, useAsync: true))
                    {
                        await CopyBoundedAsync(
                            entry.DataStream, output, entry.Length, cancellationToken);
                    }
                    break;

                default:
                    throw new SourceValidationException(
                        $"Archive entry type '{entry.EntryType}' is not permitted.");
            }
        }
    }

    public void ValidateTree(string rootDirectory)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(rootDirectory));
        long totalBytes = 0;
        var count = 0;

        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                count++;
                if (count > _maxFileCount)
                    throw new SourceValidationException("Source tree exceeds the maximum file count.");
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new SourceValidationException("Source tree links are not permitted.");

                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                    continue;
                }

                totalBytes = checked(totalBytes + ((FileInfo)entry).Length);
                if (totalBytes > _maxExpandedBytes)
                    throw new SourceValidationException("Source tree exceeds the expanded-size limit.");
            }
        }
    }

    public void ValidateFile(string filePath)
    {
        var file = new FileInfo(filePath);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new SourceValidationException("Source file links are not permitted.");
        if (file.Length > _maxExpandedBytes)
            throw new SourceValidationException("Source file exceeds the size limit.");
    }

    public static string RedactUri(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.ToString();
    }

    private SocketsHttpHandler CreatePinnedHandler(IReadOnlyList<IPAddress> addresses)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = async (context, cancellationToken) =>
            {
                Exception? lastError = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(
                            new IPEndPoint(address, context.DnsEndPoint.Port),
                            cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        lastError = ex;
                        socket.Dispose();
                    }
                }

                throw new HttpRequestException(
                    "Could not connect to an approved source address.", lastError);
            }
        };
    }

    private static Stream OpenTarPayload(string archivePath, Stream archive)
    {
        var lower = archivePath.ToLowerInvariant();
        if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz"))
            return new GZipStream(archive, CompressionMode.Decompress, leaveOpen: false);
        if (lower.EndsWith(".tar"))
            return archive;
        throw new SourceValidationException(
            "Only .tar, .tar.gz, and .tgz archives are permitted.");
    }

    private string ResolveArchivePath(string root, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) ||
            entryName.Contains('\\') ||
            Path.IsPathRooted(entryName))
        {
            throw new SourceValidationException("Archive contains an invalid path.");
        }

        var rawSegments = entryName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (rawSegments.Any(segment => segment == ".."))
            throw new SourceValidationException("Archive path traversal is not permitted.");
        var segments = rawSegments.Where(segment => segment != ".").ToArray();
        if (segments.Length == 0)
            throw new SourceValidationException("Archive contains an invalid path.");

        var destination = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        if (!destination.StartsWith(root, StringComparison.Ordinal))
            throw new SourceValidationException("Archive path escapes the extraction root.");
        return destination;
    }

    private void AssertExpansionWithinLimits(long expandedBytes, long compressedBytes)
    {
        if (expandedBytes > _maxExpandedBytes)
            throw new SourceValidationException("Archive exceeds the expanded-size limit.");
        var ratioLimit = checked(Math.Max(compressedBytes, 1) * (long)_maxCompressionRatio);
        if (expandedBytes > ratioLimit)
            throw new SourceValidationException("Archive exceeds the compression-ratio limit.");
    }

    private static async Task CopyBoundedAsync(
        Stream input,
        Stream output,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            copied = checked(copied + read);
            if (copied > maximumBytes)
                throw new SourceValidationException("Source exceeded its byte limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
}
