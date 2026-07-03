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

    /// <summary>
    /// Outcome of a single Trivy invocation (server or CLI). Carries the parsed
    /// vulnerabilities, the raw scanner output (for diagnosis), and — critically
    /// — a <see cref="Success"/> flag that distinguishes "scan ran and found
    /// nothing" from "we could not understand the scanner's output". The previous
    /// design returned a bare <c>List&lt;Vulnerability&gt;</c>, which made a parse
    /// failure indistinguishable from a clean scan and let unsigned artifacts
    /// through the signing gate.
    /// </summary>
    private sealed class TrivyScanResult
    {
        public List<Vulnerability> Vulnerabilities { get; init; } = new();
        /// <summary>Raw JSON exactly as Trivy returned it. Persisted on both success and failure.</summary>
        public string? RawOutput { get; init; }
        /// <summary>Non-null when parsing or invocation failed.</summary>
        public string? Error { get; init; }
        public bool Success => Error == null;
    }

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
        client.Timeout = TimeSpan.FromMinutes(10);

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
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "trivy",
                ArgumentList = { "fs", "--format", "json", "--output", tempReport, "--exit-code", "0", "--no-progress", artifactPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start trivy process");

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                stderr = await process.StandardError.ReadToEndAsync();
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
    /// Parse Trivy Server API JSON response into Vulnerability objects.
    /// </summary>
    private TrivyScanResult ParseTrivyServerResponse(string jsonResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            var root = doc.RootElement;
            var vulnerabilities = new List<Vulnerability>();

            // Trivy Server response has a "results" array
            if (root.TryGetProperty("results", out var results))
            {
                foreach (var result in results.EnumerateArray())
                {
                    if (result.TryGetProperty("vulnerabilities", out var vulns))
                    {
                        foreach (var vuln in vulns.EnumerateArray())
                            vulnerabilities.Add(ParseVulnerability(vuln));
                    }
                }
            }
            // Sometimes the response is a direct array of results
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var result in root.EnumerateArray())
                {
                    if (result.TryGetProperty("vulnerabilities", out var vulns))
                    {
                        foreach (var vuln in vulns.EnumerateArray())
                            vulnerabilities.Add(ParseVulnerability(vuln));
                    }
                }
            }
            // A well-formed Trivy payload may legitimately contain zero results
            // (e.g. an empty array or an object with no "results" key). That is a
            // clean scan, NOT a parse failure — distinguishing the two is the
            // whole point of carrying Success separately from the vuln count.
            return new TrivyScanResult { Vulnerabilities = vulnerabilities, RawOutput = jsonResponse };
        }
        catch (Exception ex)
        {
            // Do NOT swallow this as "0 vulnerabilities". Return failure so the
            // report is marked Failed and the signing gate blocks.
            _logger.LogWarning(ex, "Failed to parse Trivy Server response");
            return new TrivyScanResult { Error = ex.Message, RawOutput = jsonResponse };
        }
    }

    /// <summary>
    /// Parse Trivy CLI JSON output into Vulnerability objects.
    /// </summary>
    private TrivyScanResult ParseTrivyCliResponse(string jsonResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            var root = doc.RootElement;
            var vulnerabilities = new List<Vulnerability>();

            // CLI output has a "Results" array
            if (root.TryGetProperty("Results", out var results))
            {
                foreach (var result in results.EnumerateArray())
                {
                    if (result.TryGetProperty("Vulnerabilities", out var vulns))
                    {
                        foreach (var vuln in vulns.EnumerateArray())
                            vulnerabilities.Add(ParseVulnerability(vuln));
                    }
                }
            }
            // See ParseTrivyServerResponse: a "Results": [] with no vulns is clean.
            return new TrivyScanResult { Vulnerabilities = vulnerabilities, RawOutput = jsonResponse };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Trivy CLI response");
            return new TrivyScanResult { Error = ex.Message, RawOutput = jsonResponse };
        }
    }

    private string GetStringProperty(JsonElement vuln, params string[] names)
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

    private Vulnerability ParseVulnerability(JsonElement vuln)
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

    public async Task<CveReport?> GetReportAsync(Guid id)
    {
        return await _cache.GetOrSetAsync(
            CacheKeys.ScanReport(id),
            async () =>
            {
                var report = await _db.CveReports
                    .Include(r => r.Vulnerabilities)
                    .FirstOrDefaultAsync(r => r.Id == id);
                return report;
            },
            TimeSpan.FromMinutes(5));
    }

    public async Task<List<CveReport>> GetArtifactReportsAsync(Guid artifactId)
    {
        return await _cache.GetOrSetAsync(
            CacheKeys.ScanArtifactReports(artifactId),
            async () => await _db.CveReports
                .Include(r => r.Vulnerabilities)
                .Where(r => r.ArtifactId == artifactId)
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync(),
            TimeSpan.FromMinutes(5));
    }

    public async Task<List<CveReport>> GetRecentReportsAsync(int count)
    {
        return await _db.CveReports
            .Include(r => r.Vulnerabilities)
            .OrderByDescending(r => r.CreatedAt)
            .Take(count)
            .ToListAsync();
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