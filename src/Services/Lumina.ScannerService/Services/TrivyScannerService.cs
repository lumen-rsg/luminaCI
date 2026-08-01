using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumina.ScannerService.Data;
using Lumina.Shared.DTOs;
using Lumina.Shared.Extensions;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.ScannerService.Services;

/// <summary>
/// Outcome of a single Trivy invocation. A successful result means the payload
/// matched a recognized Trivy schema, even when no vulnerabilities were found.
/// </summary>
internal sealed class TrivyScanResult
{
    public List<Vulnerability> Vulnerabilities { get; init; } = new();
    public string? RawOutput { get; init; }
    public string? Error { get; init; }
    public bool Success => Error == null;
}

/// <summary>
/// Trivy scanner service that uses the embedded Trivy CLI in client/server mode.
/// RPM artifacts are installed without scripts into an isolated root filesystem
/// so Trivy can inspect the package database without executing package content.
/// </summary>
public class TrivyScannerService
{
    private readonly ScannerDbContext _db;
    private readonly ILogger<TrivyScannerService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly RedisCacheService _cache;
    private readonly string _artifactsRoot;
    private readonly TimeSpan _scanTimeout;

    public TrivyScannerService(
        ScannerDbContext db,
        ILogger<TrivyScannerService> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        RedisCacheService cache)
    {
        _db = db;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _cache = cache;
        // Artifacts are shared from the host at /opt/lumina/builds and mounted
        // into the service at /app/builds. Only files under this root may be
        // scanned — never hand a client-supplied absolute path to trivy, which
        // would otherwise read and report on arbitrary filesystem locations.
        _artifactsRoot = config["Builds:ArtifactsRoot"] ?? "/app/builds";
        _scanTimeout = TimeSpan.FromSeconds(
            int.TryParse(config["Trivy:TimeoutSeconds"], out var timeoutSeconds) && timeoutSeconds > 0
                ? timeoutSeconds
                : 600);
    }

    /// <summary>
    /// Scan an artifact using Trivy. Creates a CveReport record.
    /// </summary>
    public async Task<CveReport> ScanArtifactAsync(
        Guid artifactId,
        string artifactPath,
        string scannerType = "Trivy",
        string? expectedSha256 = null,
        long? expectedFileSize = null)
    {
        // SECURITY: confine the client-supplied path to the trusted artifacts
        // root before it reaches trivy (CLI or Server API). Without this an
        // authenticated caller could point the scanner at arbitrary paths and
        // read filesystem contents via the vulnerability report.
        var safePath = ProcessArgumentSanitizer.ResolveConfinedPath(artifactPath, _artifactsRoot);
        var snapshotDirectory = Path.Combine(Path.GetTempPath(), "lumina-scans", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshotDirectory);
        var snapshotPath = Path.Combine(snapshotDirectory, Path.GetFileName(safePath));
        try
        {
            await using (var source = new FileStream(
                safePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var snapshot = new FileStream(
                snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(snapshot);
            }

            var snapshotSize = new FileInfo(snapshotPath).Length;
            await using var hashStream = File.OpenRead(snapshotPath);
            var snapshotSha256 = Convert.ToHexString(await SHA256.HashDataAsync(hashStream))
                .ToLowerInvariant();

            if (expectedFileSize is not null && snapshotSize != expectedFileSize)
                throw new InvalidOperationException("Artifact size changed before CVE scanning.");
            if (expectedSha256 is not null &&
                !string.Equals(snapshotSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Artifact digest mismatch before CVE scanning: expected {expectedSha256}, got {snapshotSha256}.");
            }

            _logger.LogInformation(
                "Starting CVE scan for artifact {ArtifactId} from verified snapshot {Digest}",
                artifactId, snapshotSha256);

            var report = await _db.CveReports
                .Include(item => item.Vulnerabilities)
                .SingleOrDefaultAsync(item => item.ArtifactId == artifactId);
            if (report is not null &&
                (report.Status is ScanStatus.Completed or ScanStatus.Failed) &&
                !string.IsNullOrWhiteSpace(report.ArtifactSha256))
            {
                if (!string.Equals(report.ArtifactSha256, snapshotSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Artifact identity changed after its CVE report was created.");
                _logger.LogInformation(
                    "Returning existing terminal CVE report {ReportId} for artifact {ArtifactId}",
                    report.Id, artifactId);
                return report;
            }

            if (report is null)
            {
                report = new CveReport
                {
                    Id = Guid.NewGuid(),
                    ArtifactId = artifactId,
                    CreatedAt = DateTime.UtcNow
                };
                _db.CveReports.Add(report);
            }
            else
            {
                _db.Vulnerabilities.RemoveRange(report.Vulnerabilities);
                report.Vulnerabilities.Clear();
            }

            report.ArtifactSha256 = snapshotSha256;
            report.ScannerType = scannerType;
            report.Status = ScanStatus.Running;
            report.CompletedAt = null;
            report.Summary = null;
            report.RawOutput = null;
            await _db.SaveChangesAsync();

            // Trivy sees only this private verified snapshot, never the shared
            // mutable build path carried by the request.
            await RunScanAsync(report, snapshotPath);

            return report;
        }
        finally
        {
            if (Directory.Exists(snapshotDirectory))
                Directory.Delete(snapshotDirectory, recursive: true);
        }
    }

    private async Task RunScanAsync(CveReport report, string artifactPath)
    {
        TrivyScanResult scanResult;
        try
        {
            var trivyServerUrl = _config["Trivy:ServerUrl"] ?? "http://trivy-server:8080";
            scanResult = await ScanViaCliAsync(artifactPath, trivyServerUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CVE scan failed for artifact {ArtifactId}", report.ArtifactId);
            FailReport(report, ex.Message, rawOutput: null);
            await _db.SaveChangesAsync();
            return;
        }

        // A parse failure must NOT be reported as a
        // clean scan. Previously a parse exception was swallowed and an empty
        // vulnerability list surfaced as "0 critical, 0 high", which the signing
        // gate treats as a pass. Fail the report instead.
        if (!scanResult.Success)
        {
            var error = scanResult.Error;
            _logger.LogError("Trivy response could not be parsed for artifact {ArtifactId}: {Error}. RawOutput will be persisted for diagnosis.",
                report.ArtifactId, error ?? "unknown error");
            FailReport(report, $"Trivy output parse failed: {error}", rawOutput: scanResult.RawOutput);
            await _db.SaveChangesAsync();
            return;
        }

        var vulnerabilities = scanResult.Vulnerabilities;

        // Update report with vulnerability counts. Severity is normalized to a
        // canonical uppercase token in ParseVulnerability, so OrdinalIgnoreCase
        // is belt-and-suspenders rather than load-bearing. Anything outside the
        // known set lands in UnknownCount and blocks signing.
        report.Status = ScanStatus.Completed;
        report.CompletedAt = DateTime.UtcNow;
        report.CriticalCount = vulnerabilities.Count(v => v.Severity.Equals("CRITICAL", StringComparison.OrdinalIgnoreCase));
        report.HighCount = vulnerabilities.Count(v => v.Severity.Equals("HIGH", StringComparison.OrdinalIgnoreCase));
        report.MediumCount = vulnerabilities.Count(v => v.Severity.Equals("MEDIUM", StringComparison.OrdinalIgnoreCase));
        report.LowCount = vulnerabilities.Count(v => v.Severity.Equals("LOW", StringComparison.OrdinalIgnoreCase));
        report.UnknownCount = vulnerabilities.Count(v => v.Severity.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase));
        report.Summary = $"Found {vulnerabilities.Count} vulnerabilities ({report.CriticalCount} critical, {report.HighCount} high, {report.UnknownCount} unknown)";
        // Persist the actual scanner output for diagnosis — not a re-serialization
        // of our parsed objects, which would hide exactly the malformed payloads
        // we'd need to debug.
        report.RawOutput = scanResult.RawOutput;

        foreach (var vuln in vulnerabilities)
        {
            vuln.CveReportId = report.Id;
            _db.Vulnerabilities.Add(vuln);
        }

        _db.CveReports.Update(report);
        await _db.SaveChangesAsync();

        // Invalidate cache
        await _cache.RemoveAsync(CacheKeys.ScanReport(report.Id));
        await _cache.RemoveAsync(CacheKeys.ScanArtifactReports(report.ArtifactId));

        _logger.LogInformation("CVE scan completed for artifact {ArtifactId}: {Count} vulnerabilities found",
            report.ArtifactId, vulnerabilities.Count);
    }

    /// <summary>Mark <paramref name="report"/> failed and record the reason + raw output.</summary>
    private void FailReport(CveReport report, string error, string? rawOutput)
    {
        report.Status = ScanStatus.Failed;
        report.CompletedAt = DateTime.UtcNow;
        report.Summary = $"Scan failed: {error}";
        report.CriticalCount = 0;
        report.HighCount = 0;
        report.MediumCount = 0;
        report.LowCount = 0;
        report.UnknownCount = 0;
        report.RawOutput = rawOutput;
        _db.CveReports.Update(report);
    }

    /// <summary>
    /// Scan using Trivy Server API (HTTP/Twirp protocol).
    /// POST to /twirp/trivy.v1.Scanner/Scan with JSON body.
    /// </summary>
    private async Task<TrivyScanResult> ScanViaServerApiAsync(string serverUrl, string artifactPath)
    {
        var client = _httpClientFactory.CreateClient("TrivyServer");
        client.BaseAddress = new Uri(serverUrl);
        client.Timeout = _scanTimeout;

        var scanRequest = new
        {
            target = artifactPath,
            artifact = new
            {
                mime_type = "application/x-rpm"
            },
            scanners = new[] { "vuln" }
        };

        var response = await client.PostAsJsonAsync("/twirp/trivy.v1.Scanner/Scan", scanRequest);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync();
        return ParseTrivyServerResponse(responseContent);
    }

    /// <summary>
    /// Scan an RPM through Trivy's rootfs mode without executing package scripts.
    /// </summary>
    private async Task<TrivyScanResult> ScanViaCliAsync(string artifactPath, string serverUrl)
    {
        if (!File.Exists(artifactPath))
        {
            _logger.LogWarning("Artifact file not found: {Path}", artifactPath);
            return new TrivyScanResult { Error = $"artifact file not found: {artifactPath}" };
        }

        var scanDirectory = Path.Combine(Path.GetTempPath(), "lumina-rootfs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scanDirectory);
        var tempReport = Path.Combine(Path.GetTempPath(), $"trivy-report-{Guid.NewGuid():N}.json");
        string? stderr = null;

        try
        {
            await RunProcessCheckedAsync(
                "rpm",
                ["--root", scanDirectory, "--dbpath", "/var/lib/rpm", "--initdb"],
                _scanTimeout);
            await RunProcessCheckedAsync(
                "rpm",
                BuildRpmInstallArguments(scanDirectory, artifactPath),
                _scanTimeout);

            var psi = new ProcessStartInfo
            {
                FileName = _config["Trivy:Path"] ?? "trivy",
                ArgumentList =
                {
                    "rootfs",
                    "--server", serverUrl,
                    "--format", "json",
                    "--output", tempReport,
                    "--exit-code", "0",
                    "--no-progress",
                    "--scanners", "vuln",
                    scanDirectory
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start trivy process");

            // Drain redirected streams while the process runs so a noisy Trivy
            // invocation cannot deadlock on a full OS pipe.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exited = await WaitForExitOrKillAsync(process, _scanTimeout);
            stderr = Truncate(await stderrTask, 4_000);
            _ = await stdoutTask;

            if (!exited)
            {
                return new TrivyScanResult
                {
                    Error = $"trivy scan exceeded {_scanTimeout.TotalSeconds:0} seconds and was terminated"
                };
            }

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Trivy CLI exited with code {Code}: {Error}", process.ExitCode, stderr);
            }

            if (!File.Exists(tempReport))
            {
                return new TrivyScanResult
                {
                    Error = $"trivy produced no report (exit code {process.ExitCode}). stderr: {stderr ?? "<none>"}"
                };
            }

            var json = await File.ReadAllTextAsync(tempReport);
            var result = ParseTrivyCliResponse(json);
            // Surface CLI stderr as a diagnostic breadcrumb but don't override a
            // successful parse; trivy writes warnings to stderr even on success.
            if (!string.IsNullOrWhiteSpace(stderr) && result.Success)
                _logger.LogDebug("Trivy CLI succeeded with stderr for artifact: {Error}", stderr);
            return result;
        }
        finally
        {
            if (File.Exists(tempReport))
                try { File.Delete(tempReport); } catch { }
            if (Directory.Exists(scanDirectory))
                try { Directory.Delete(scanDirectory, recursive: true); } catch { }
        }
    }

    internal static IReadOnlyList<string> BuildRpmInstallArguments(
        string scanDirectory,
        string artifactPath) =>
    [
        "--root", scanDirectory,
        "--dbpath", "/var/lib/rpm",
        "--install",
        "--nodeps",
        "--noscripts",
        "--notriggers",
        "--ignorearch",
        artifactPath
    ];

    private static async Task RunProcessCheckedAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!await WaitForExitOrKillAsync(process, timeout))
            throw new TimeoutException($"{fileName} exceeded the scan timeout.");

        var stdout = Truncate(await stdoutTask, 4_000);
        var stderr = Truncate(await stderrTask, 4_000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} exited with code {process.ExitCode}: {stderr}{stdout}");
        }
    }

    /// <summary>
    /// Wait for a child process within a fixed budget. On timeout or caller
    /// cancellation, terminate and reap the complete process tree.
    /// </summary>
    internal static async Task<bool> WaitForExitOrKillAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            using var reapCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(reapCts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Process {process.Id} did not exit after its process tree was terminated");
            }

            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();

            return false;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // It may exit between HasExited and Kill. WaitForExitAsync below
            // still reaps it.
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// Parse Trivy Server API JSON response into Vulnerability objects.
    /// </summary>
    internal static TrivyScanResult ParseTrivyServerResponse(string jsonResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            var root = doc.RootElement;
            var vulnerabilities = new List<Vulnerability>();
            JsonElement results;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (!root.TryGetProperty("results", out results) || results.ValueKind != JsonValueKind.Array)
                {
                    return InvalidTrivyPayload(jsonResponse, "server response must contain a results array");
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                results = root;
            }
            else
            {
                return InvalidTrivyPayload(jsonResponse, "server response must be an object or result array");
            }

            var parseError = ParseResults(results, "vulnerabilities", vulnerabilities);
            if (parseError != null)
                return InvalidTrivyPayload(jsonResponse, parseError);

            return new TrivyScanResult { Vulnerabilities = vulnerabilities, RawOutput = jsonResponse };
        }
        catch (Exception ex)
        {
            return new TrivyScanResult { Error = ex.Message, RawOutput = jsonResponse };
        }
    }

    /// <summary>
    /// Parse Trivy CLI JSON output into Vulnerability objects.
    /// </summary>
    internal static TrivyScanResult ParseTrivyCliResponse(string jsonResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            var root = doc.RootElement;
            var vulnerabilities = new List<Vulnerability>();

            if (root.ValueKind != JsonValueKind.Object)
            {
                return InvalidTrivyPayload(jsonResponse, "CLI response must contain a Results array");
            }

            if (!root.TryGetProperty("Results", out var results))
            {
                // Trivy omits Results entirely when a valid scan finds no
                // vendor-supported package targets (common for internally built
                // RPMs). Do not confuse that documented output shape with an
                // arbitrary JSON object: require the complete client/server
                // report envelope before accepting it as clean.
                return IsCompleteEmptyCliReport(root)
                    ? new TrivyScanResult { Vulnerabilities = vulnerabilities, RawOutput = jsonResponse }
                    : InvalidTrivyPayload(jsonResponse, "CLI response must contain a Results array");
            }

            if (results.ValueKind != JsonValueKind.Array)
                return InvalidTrivyPayload(jsonResponse, "CLI Results must be an array");

            var parseError = ParseResults(results, "Vulnerabilities", vulnerabilities);
            if (parseError != null)
                return InvalidTrivyPayload(jsonResponse, parseError);

            return new TrivyScanResult { Vulnerabilities = vulnerabilities, RawOutput = jsonResponse };
        }
        catch (Exception ex)
        {
            return new TrivyScanResult { Error = ex.Message, RawOutput = jsonResponse };
        }
    }

    private static bool IsCompleteEmptyCliReport(JsonElement root)
    {
        if (!root.TryGetProperty("SchemaVersion", out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out var schema) ||
            schema < 2 ||
            !root.TryGetProperty("ArtifactType", out var artifactType) ||
            artifactType.ValueKind != JsonValueKind.String ||
            artifactType.GetString() != "filesystem" ||
            !root.TryGetProperty("CreatedAt", out var createdAt) ||
            createdAt.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("Trivy", out var trivy) ||
            trivy.ValueKind != JsonValueKind.Object ||
            !trivy.TryGetProperty("Version", out var clientVersion) ||
            clientVersion.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(clientVersion.GetString()) ||
            !trivy.TryGetProperty("Server", out var server) ||
            server.ValueKind != JsonValueKind.Object ||
            !server.TryGetProperty("Version", out var serverVersion) ||
            serverVersion.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(serverVersion.GetString()) ||
            !server.TryGetProperty("VulnerabilityDB", out var vulnerabilityDb) ||
            vulnerabilityDb.ValueKind != JsonValueKind.Object ||
            !vulnerabilityDb.TryGetProperty("UpdatedAt", out var databaseUpdatedAt) ||
            databaseUpdatedAt.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("Metadata", out var metadata) ||
            metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("OS", out var os) ||
            os.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return true;
    }

    private static string? ParseResults(
        JsonElement results,
        string vulnerabilitiesProperty,
        List<Vulnerability> vulnerabilities)
    {
        foreach (var result in results.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object)
                return "each result must be an object";

            if (!result.TryGetProperty(vulnerabilitiesProperty, out var vulns) ||
                vulns.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (vulns.ValueKind != JsonValueKind.Array)
                return $"{vulnerabilitiesProperty} must be an array or null";

            foreach (var vuln in vulns.EnumerateArray())
            {
                if (vuln.ValueKind != JsonValueKind.Object)
                    return "each vulnerability must be an object";

                vulnerabilities.Add(ParseVulnerability(vuln));
            }
        }

        return null;
    }

    private static TrivyScanResult InvalidTrivyPayload(string rawOutput, string error) =>
        new() { Error = error, RawOutput = rawOutput };

    private static string GetStringProperty(JsonElement vuln, params string[] names)
    {
        foreach (var name in names)
        {
            if (vuln.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var val = prop.GetString();
                if (val != null) return val;
            }
        }
        return "";
    }

    private static Vulnerability ParseVulnerability(JsonElement vuln)
    {
        var cveId = GetStringProperty(vuln, "VulnerabilityID", "vulnerability_id");
        var severity = GetStringProperty(vuln, "Severity", "severity");
        var title = GetStringProperty(vuln, "Title", "title");
        // Read the real Description field, falling back to Title (Trivy sometimes
        // omits Description). The old code set Description = Title unconditionally
        // and discarded the actual advisory text.
        var description = GetStringProperty(vuln, "Description", "description");
        if (string.IsNullOrWhiteSpace(description))
            description = title;

        var pkg = GetStringProperty(vuln, "PkgName", "pkg_name");

        return new Vulnerability
        {
            Id = Guid.NewGuid(),
            CveId = string.IsNullOrEmpty(cveId) ? "unknown" : cveId,
            Package = pkg,
            PackageName = pkg,
            Title = title,
            Severity = NormalizeSeverity(severity),
            InstalledVersion = GetStringProperty(vuln, "InstalledVersion", "installed_version"),
            FixedVersion = GetStringProperty(vuln, "FixedVersion", "fixed_version"),
            Description = description,
            PublishedDate = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Map a raw severity string to a canonical token. Unknown/empty values map
    /// to <c>UNKNOWN</c> rather than being passed through verbatim, so an
    /// unrecognized severity is counted in <c>UnknownCount</c> and conservatively
    /// blocks signing — instead of silently bypassing the Critical/High gate.
    /// </summary>
    private static string NormalizeSeverity(string severity)
    {
        if (string.IsNullOrWhiteSpace(severity))
            return "UNKNOWN";

        return severity.Trim().ToUpperInvariant() switch
        {
            "CRITICAL" => "CRITICAL",
            "HIGH" => "HIGH",
            "MEDIUM" => "MEDIUM",
            "LOW" => "LOW",
            // Trivy occasionally emits "UNKNOWN" for un-scored advisories; keep it
            // in its own bucket so it blocks signing instead of being ignored.
            "UNKNOWN" => "UNKNOWN",
            _ => "UNKNOWN"
        };
    }

    /// <summary>
    /// Get paginated scan results with vulnerability counts.
    /// </summary>
    public async Task<ScanPaginatedResponse> GetScansPaginatedAsync(int page, int pageSize)
    {
        var totalCount = await _db.CveReports.CountAsync();
        var reports = await _db.CveReports
            .Include(r => r.Vulnerabilities)
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new ScanSummaryResponse(
                r.Id,
                r.ArtifactId,
                r.ScannerType,
                r.Status,
                r.Vulnerabilities.Count,
                r.Vulnerabilities.Count(v => v.Severity == "CRITICAL"),
                r.Vulnerabilities.Count(v => v.Severity == "HIGH"),
                r.CreatedAt,
                r.CompletedAt
            ))
            .ToListAsync();

        return new ScanPaginatedResponse(reports, totalCount, page, pageSize);
    }
}
