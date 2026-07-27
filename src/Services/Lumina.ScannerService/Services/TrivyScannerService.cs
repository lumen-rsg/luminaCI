using System.Diagnostics;
using System.Net.Http.Json;
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
/// Trivy scanner service that uses the Trivy Server HTTP API (/twirp/trivy.v1.Scanner/Scan).
/// Requires TRIVY_SERVER_URL to be configured (e.g., http://trivy-server:8080).
/// Falls back to CLI mode if server is unavailable.
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
    /// Scan an artifact using Trivy Server API. Creates a CveReport record.
    /// </summary>
    public async Task<CveReport> ScanArtifactAsync(Guid artifactId, string artifactPath, string scannerType = "Trivy")
    {
        // SECURITY: confine the client-supplied path to the trusted artifacts
        // root before it reaches trivy (CLI or Server API). Without this an
        // authenticated caller could point the scanner at arbitrary paths and
        // read filesystem contents via the vulnerability report.
        var safePath = ProcessArgumentSanitizer.ResolveConfinedPath(artifactPath, _artifactsRoot);

        _logger.LogInformation("Starting CVE scan for artifact {ArtifactId} at {Path}", artifactId, safePath);

        var report = new CveReport
        {
            Id = Guid.NewGuid(),
            ArtifactId = artifactId,
            ScannerType = scannerType,
            Status = ScanStatus.Running,
            CreatedAt = DateTime.UtcNow
        };

        _db.CveReports.Add(report);
        await _db.SaveChangesAsync();

        // IMPORTANT: await the scan so the consumer gets the completed report
        // with actual vulnerability counts before publishing CveScanCompleted.
        // Previously this was fire-and-forget (_ = RunScanAsync), which caused
        // the consumer to publish CveScanCompleted with Status=Running and 0 counts.
        await RunScanAsync(report, safePath);

        return report;
    }

    private async Task RunScanAsync(CveReport report, string artifactPath)
    {
        TrivyScanResult scanResult;
        string? scanError = null;

        try
        {
            var trivyServerUrl = _config["Trivy:ServerUrl"] ?? "http://trivy-server:8080";

            // Try Trivy Server API first
            TrivyScanResult serverResult;
            try
            {
                serverResult = await ScanViaServerApiAsync(trivyServerUrl, artifactPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trivy Server API failed, falling back to CLI for artifact {ArtifactId}", report.ArtifactId);
                serverResult = new TrivyScanResult { Error = ex.Message };
            }

            // Fall back to CLI only when the server call itself threw or reported
            // failure. If the server succeeded (even with zero vulns) we trust it.
            if (serverResult.Success)
            {
                scanResult = serverResult;
            }
            else
            {
                scanResult = await ScanViaCliAsync(artifactPath);
                // Preserve the server-side failure reason for the report summary.
                if (!scanResult.Success && !string.IsNullOrEmpty(serverResult.Error))
                    scanError = $"server: {serverResult.Error}; cli: {scanResult.Error}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CVE scan failed for artifact {ArtifactId}", report.ArtifactId);
            FailReport(report, ex.Message, rawOutput: null);
            await _db.SaveChangesAsync();
            return;
        }

        // A parse failure (after server + CLI fallback) must NOT be reported as a
        // clean scan. Previously a parse exception was swallowed and an empty
        // vulnerability list surfaced as "0 critical, 0 high", which the signing
        // gate treats as a pass. Fail the report instead.
        if (!scanResult.Success)
        {
            var error = string.IsNullOrEmpty(scanError) ? scanResult.Error : scanError;
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
    /// Fallback: scan using Trivy CLI with Process.Start.
    /// </summary>
    private async Task<TrivyScanResult> ScanViaCliAsync(string artifactPath)
    {
        if (!File.Exists(artifactPath))
        {
            _logger.LogWarning("Artifact file not found: {Path}", artifactPath);
            return new TrivyScanResult { Error = $"artifact file not found: {artifactPath}" };
        }

        var tempReport = Path.Combine(Path.GetTempPath(), $"trivy-report-{Guid.NewGuid():N}.json");
        string? stderr = null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _config["Trivy:Path"] ?? "trivy",
                ArgumentList = { "fs", "--format", "json", "--output", tempReport, "--exit-code", "0", "--no-progress", artifactPath },
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

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                return InvalidTrivyPayload(jsonResponse, "CLI response must contain a Results array");
            }

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
