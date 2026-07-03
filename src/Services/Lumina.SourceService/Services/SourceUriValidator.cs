using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Lumina.Shared.Extensions;
using Lumina.Shared.Models.Enums;
using Microsoft.Extensions.Configuration;

namespace Lumina.SourceService.Services;

/// <summary>
/// Thrown when a configured source fails safety validation. Controllers map
/// this to HTTP 400 so an unsafe source never reaches the fetch pipeline.
/// </summary>
public class SourceValidationException : Exception
{
    public SourceValidationException(string message) : base(message) { }
}

/// <summary>
/// The result of validating a source: the value to actually use downstream.
/// For host-based sources this is the canonical URI string; for local sources
/// it is the path already confined to the trusted root.
/// </summary>
public record ValidatedSource(string Url, SourceType SourceType, string? Branch, string? LocalPath);

/// <summary>
/// Validates a source before it is handed to git/curl/rsync/svn/hg/file-copy.
/// This is the central SSRF and arbitrary-file-read defense for the source
/// pipeline: <c>conf.ini</c> is user-writable via PUT/POST endpoints, so the
/// raw <c>source</c>/<c>source_type</c> values can never be trusted as-is.
///
/// Defense layers:
///  * Scheme allow-list per source type — rejects <c>file://</c> on host-based
///    fetchers and mismatched protocols.
///  * Host allow-list — when <c>Source:AllowedHosts</c> is set, only those
///    hosts are permitted.
///  * Private/loopback/link-local range block — resolves the host and refuses
///    addresses that point at the container network (minio/rabbitmq/postgres)
///    or the cloud metadata endpoint (169.254.169.254).
///  * Local confinement — <c>SourceType.Local</c> is disabled in production by
///    default; when enabled it is confined to <c>Source:LocalSourcesRoot</c>
///    via <see cref="ProcessArgumentSanitizer.ResolveConfinedPath"/>.
/// </summary>
public class SourceUriValidator
{
    private readonly ILogger<SourceUriValidator> _logger;
    private readonly HashSet<string> _allowedHosts;
    private readonly bool _blockPrivateRanges;
    private readonly bool _allowLocalSources;
    private readonly string _localSourcesRoot;

    // IPv4 special-use ranges, expressed as (network, mask) big-endian uints.
    private static readonly (uint Net, uint Mask)[] Ipv4Blocked =
    {
        (0x7F000000, 0xFF000000), // 127.0.0.0/8        loopback
        (0xA9FE0000, 0xFFFF0000), // 169.254.0.0/16     link-local (metadata endpoint)
        (0x0A000000, 0xFF000000), // 10.0.0.0/8         private
        (0xAC100000, 0xFFF00000), // 172.16.0.0/12      private
        (0xC0A80000, 0xFFFF0000), // 192.168.0.0/16     private
        (0xE0000000, 0xF0000000), // 224.0.0.0/4        multicast
        (0x00000000, 0xFF000000), // 0.0.0.0/8          "this network" / unspecified
    };

    public SourceUriValidator(IConfiguration config, ILogger<SourceUriValidator> logger)
    {
        _logger = logger;
        _allowedHosts = (config["Source:AllowedHosts"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => h.ToLowerInvariant())
            .ToHashSet();
        _blockPrivateRanges = !bool.TryParse(config["Source:BlockPrivateNetworkRanges"], out var block) || block;
        _allowLocalSources = bool.TryParse(config["Source:AllowLocalSources"], out var allow) && allow;
        _localSourcesRoot = config["Source:LocalSourcesRoot"] ?? "/app/local-sources";
    }

    /// <summary>
    /// Validate a source. Throws <see cref="SourceValidationException"/> on any
    /// policy violation; otherwise returns the canonicalized value to use.
    /// </summary>
    public async Task<ValidatedSource> ValidateAsync(string url, SourceType type, string? branch)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new SourceValidationException("Source URL/path is empty.");

        // Local sources are handled completely differently: no URI, no DNS.
        if (type == SourceType.Local)
            return ValidateLocal(url);

        // Reject host-control / shell metacharacters up front: these values are
        // also forwarded to external processes via ArgumentList (which is not
        // shell-vulnerable), but rejecting metacharacters here keeps malicious
        // branch names from even reaching the tools.
        //
        // Branch names are validated with the same allowlist the source fetcher
        // uses (ProcessArgumentSanitizer.ValidateGitReference) — no denylist to
        // bypass and no legitimate ref silently rejected.
        string? safeBranch = null;
        if (branch is not null)
            safeBranch = ProcessArgumentSanitizer.ValidateGitReference(branch);

        // The URL itself is parsed by Uri.TryCreate below; before that, only
        // reject control characters (NUL/newline/tab). The old denylist rejected
        // '(' and other characters that can appear in legitimate query strings.
        AssertNoControlCharacters(url, "Source URL");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new SourceValidationException(
                $"Source must be an absolute URI. Got: {url}");

        var scheme = uri.Scheme.ToLowerInvariant();
        if (!IsSchemeAllowed(type, scheme))
            throw new SourceValidationException(
                $"Scheme '{scheme}' is not permitted for source type {type}.");

        var host = uri.Host;
        if (string.IsNullOrEmpty(host))
            throw new SourceValidationException($"Source URI is missing a host: {url}");

        // Host allow-list — exact, case-insensitive match.
        if (_allowedHosts.Count > 0 && !_allowedHosts.Contains(host.ToLowerInvariant()))
            throw new SourceValidationException(
                $"Source host '{host}' is not in the allow-list.");

        // Resolve and block private/loopback/link-local ranges.
        if (_blockPrivateRanges)
            await AssertHostPublicAsync(host, url);

        return new ValidatedSource(uri.ToString(), type, safeBranch, null);
    }

    private ValidatedSource ValidateLocal(string path)
    {
        if (!_allowLocalSources)
            throw new SourceValidationException(
                "Local sources are disabled (Source:AllowLocalSources=false). " +
                "Use a host-based source type (git/http/rsync/...) instead.");

        // Confine to the trusted root so a client cannot read /etc, /app/.gnupg,
        // or any other host path by copy->tarball->upload->download.
        var confined = ProcessArgumentSanitizer.ResolveConfinedPath(path, _localSourcesRoot);
        return new ValidatedSource(confined, SourceType.Local, null, confined);
    }

    private static bool IsSchemeAllowed(SourceType type, string scheme) => type switch
    {
        SourceType.Git => scheme is "http" or "https" or "git" or "ssh",
        SourceType.Tar or SourceType.Http or SourceType.Ftp
            => scheme is "http" or "https" or "ftp",
        SourceType.Rsync => scheme is "rsync",
        SourceType.Svn => scheme is "http" or "https" or "svn" or "svn+ssh",
        SourceType.Hg => scheme is "http" or "https" or "ssh",
        _ => false
    };

    private async Task AssertHostPublicAsync(string host, string originalUrl)
    {
        // Literal IP in the host: validate directly without DNS.
        if (IPAddress.TryParse(host, out var literal))
        {
            if (IsBlockedAddress(literal))
                throw new SourceValidationException(
                    $"Source host '{host}' resolves to a private/loopback/link-local address.");
            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host);
        }
        catch (Exception ex)
        {
            throw new SourceValidationException(
                $"Could not resolve source host '{host}': {ex.Message}");
        }

        if (addresses.Length == 0)
            throw new SourceValidationException($"Source host '{host}' did not resolve.");

        foreach (var addr in addresses)
        {
            if (IsBlockedAddress(addr))
                throw new SourceValidationException(
                    $"Source host '{host}' resolves to a private/loopback/link-local " +
                    $"address ({addr}). URL: {originalUrl}");
        }
    }

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes.Length != 4) return true;
            uint ip = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16)
                      | ((uint)bytes[2] << 8) | bytes[3];
            foreach (var (net, mask) in Ipv4Blocked)
            {
                if ((ip & mask) == net)
                    return true;
            }
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // ::1 loopback, fe80::/10 link-local, fc00::/7 unique-local, ff00::/8 multicast.
            if (address.IsIPv6LinkLocal) return true;
            var bytes = address.GetAddressBytes();
            if (bytes.Length != 16) return true;
            if (IsLoopbackV6(bytes)) return true;
            if (IsUniqueLocalV6(bytes)) return true;
            if (bytes[0] == 0xFF) return true; // multicast
            return false;
        }

        // Unknown family — fail closed.
        return true;
    }

    private static bool IsLoopbackV6(byte[] b) =>
        b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0 && b[4] == 0 && b[5] == 0
        && b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0
        && b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] == 1;

    private static bool IsUniqueLocalV6(byte[] b) =>
        (b[0] & 0xFE) == 0xFC; // fc00::/7

    private static void AssertNoControlCharacters(string value, string fieldName)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
                throw new SourceValidationException(
                    $"{fieldName} contains control character U+{((int)c).ToString("X4", CultureInfo.InvariantCulture)}.");
        }
    }
}
