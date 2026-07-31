using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Lumina.Shared.Errors;

namespace Lumina.BuildService.Services.PackageGraph;

public static class RepositorySnapshotArchiveReader
{
    public const long MaxExpandedBytes = 8L * 1024 * 1024 * 1024;
    public const int MaxEntries = 200_000;
    public const int MaxCompressionRatio = 200;

    public static async Task<string> ReadManifestAsync(
        Stream compressedArchive,
        long expectedCompressedSize,
        string expectedSha256,
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compressedArchive);
        if (expectedCompressedSize <= 0)
            throw new ValidationException("Repository snapshot size must be positive.");
        if (expectedSha256.Length != 64 || expectedSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ValidationException("Repository snapshot SHA-256 is invalid.");
        var normalizedManifestPath = NormalizeExpectedPath(manifestPath);

        await using var verifiedStream = new CountingHashReadStream(
            compressedArchive, expectedCompressedSize);
        await using var gzip = new GZipStream(verifiedStream, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new TarReader(gzip, leaveOpen: true);
        byte[]? manifestBytes = null;
        long expandedBytes = 0;
        var entryCount = 0;
        var ratioLimit = expectedCompressedSize > long.MaxValue / MaxCompressionRatio
            ? long.MaxValue
            : expectedCompressedSize * MaxCompressionRatio;
        var expandedLimit = Math.Min(MaxExpandedBytes, ratioLimit);

        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryCount++;
            if (entryCount > MaxEntries)
                throw new ValidationException("Repository snapshot exceeds the maximum entry count.");

            var entryPath = NormalizeArchivePath(entry.Name);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    continue;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    break;
                default:
                    throw new ValidationException(
                        $"Repository snapshot contains forbidden tar entry type '{entry.EntryType}'.");
            }

            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > expandedLimit)
                throw new ValidationException("Repository snapshot exceeds the expansion limit.");

            if (!MatchesManifest(entryPath, normalizedManifestPath))
                continue;
            if (manifestBytes is not null)
                throw new ValidationException("Repository snapshot contains more than one matching manifest.");
            if (entry.Length > PackageGraphManifestLoader.MaxManifestBytes)
                throw new ValidationException("Repository package manifest exceeds its size limit.");
            if (entry.DataStream is null)
                throw new ValidationException("Repository package manifest has no data stream.");

            manifestBytes = new byte[checked((int)entry.Length)];
            await entry.DataStream.ReadExactlyAsync(manifestBytes, cancellationToken);
        }

        if (verifiedStream.BytesRead != expectedCompressedSize)
            throw new ValidationException("Repository snapshot size does not match immutable metadata.");
        var actualHash = verifiedStream.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(
                actualHash,
                Convert.FromHexString(expectedSha256)))
        {
            throw new ValidationException("Repository snapshot digest does not match immutable metadata.");
        }
        if (manifestBytes is null)
            throw new ValidationException($"Repository snapshot does not contain '{normalizedManifestPath}'.");

        try
        {
            return new UTF8Encoding(false, true).GetString(manifestBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ValidationException("Repository package manifest is not valid UTF-8.", exception);
        }
    }

    private static string NormalizeExpectedPath(string path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        ValidateSegments(normalized, "Manifest path");
        return normalized;
    }

    private static string NormalizeArchivePath(string path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        normalized = normalized.TrimEnd('/');
        ValidateSegments(normalized, "Archive entry path");
        return normalized;
    }

    private static void ValidateSegments(string path, string label)
    {
        if (path.Length is < 1 or > 4096 || path.StartsWith('/') ||
            path.Split('/').Any(segment => segment is "" or "." or "..") ||
            path.Any(char.IsControl))
        {
            throw new ValidationException($"{label} is not a safe repository-relative path.");
        }
    }

    private static bool MatchesManifest(string entryPath, string manifestPath) =>
        string.Equals(entryPath, manifestPath, StringComparison.Ordinal) ||
        entryPath.EndsWith('/' + manifestPath, StringComparison.Ordinal);

    private sealed class CountingHashReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public CountingHashReadStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }
        public long BytesRead { get; private set; }

        public byte[] GetHashAndReset() => _hash.GetHashAndReset();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            Record(buffer.AsSpan(offset, read));
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            Record(buffer[..read]);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            Record(buffer.Span[..read]);
            return read;
        }

        private void Record(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0)
                return;
            _hash.AppendData(bytes);
            BytesRead = checked(BytesRead + bytes.Length);
            if (BytesRead > _maxBytes)
                throw new ValidationException("Repository snapshot exceeds its immutable size.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _hash.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
