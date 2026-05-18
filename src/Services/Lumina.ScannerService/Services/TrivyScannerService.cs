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
    }

    /// <summary>
    /// Scan an artifact using Trivy Server API. Creates a CveReport record.
    /// </summary>
    public async Task<CveReport> ScanArtifactAsync(Guid artifactId, string artifactPath, string scannerType = "Trivy")
    {
        _logger.LogInformation("Starting CVE scan for artifact {ArtifactId} at {Path}", artifactId, artifactPath);

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

        // Run scan in background
        _ = RunScanAsync(report, artifactPath);

        return report;
    }

    private async Task RunScanAsync(CveReport report, string artifactPath)
    {
        try
        {
            var trivyServerUrl = _config["Trivy:ServerUrl"] ?? "http://trivy-server:8080";

            List<Vulnerability>? vulnerabilities;

            // Try Trivy Server API first
            try
            {
                vulnerabilities = await ScanViaServerApiAsync(trivyServerUrl, artifactPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trivy Server API failed, falling back to CLI for artifact {ArtifactId}", report.ArtifactId);
                vulnerabilities = await ScanViaCliAsync(artifactPath);
            }

            // Update report
            report.Status = ScanStatus.Completed;
            report.CompletedAt = DateTime.UtcNow;
            report.Summary = $"Found {vulnerabilities.Count} vulnerabilities";
            report.RawOutput = JsonSerializer.Serialize(vulnerabilities);

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "CVE scan failed for artifact {ArtifactId}", report.ArtifactId);
            report.Status = ScanStatus.Failed;
            report.CompletedAt = DateTime.UtcNow;
            report.Summary = $"Scan failed: {ex.Message}";
            _db.CveReports.Update(report);
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Scan using Trivy Server API (HTTP/Twirp protocol).
    /// POST to /twirp/trivy.v1.Scanner/Scan with JSON body.
    /// </summary>
    private async Task<List<Vulnerability>> ScanViaServerApiAsync(string serverUrl, string artifactPath)
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
    private async Task<List<Vulnerability>> ScanViaCliAsync(string artifactPath)
    {
        var vulnerabilities = new List<Vulnerability>();

        if (!File.Exists(artifactPath))
        {
            _logger.LogWarning("Artifact file not found: {Path}", artifactPath);
            return vulnerabilities;
        }

        var tempReport = Path.Combine(Path.GetTempPath(), $"trivy-report-{Guid.NewGuid():N}.json");

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "trivy",
                ArgumentList = { "image", "--format", "json", "--output", tempReport, "--exit-code", "0", "--no-progress", artifactPath },
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
                var stderr = await process.StandardError.ReadToEndAsync();
                _logger.LogWarning("Trivy CLI exited with code {Code}: {Error}", process.ExitCode, stderr);
            }

            if (File.Exists(tempReport))
            {
                var json = await File.ReadAllTextAsync(tempReport);
                vulnerabilities = ParseTrivyCliResponse(json);
            }
        }
        finally
        {
            if (File.Exists(tempReport))
                try { File.Delete(tempReport); } catch { }
        }

        return vulnerabilities;
    }

    /// <summary>
    /// Parse Trivy Server API JSON response into Vulnerability objects.
    /// </summary>
    private List<Vulnerability> ParseTrivyServerResponse(string jsonResponse)
    {
        var vulnerabilities = new List<Vulnerability>();

        try
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            var root = doc.RootElement;

            // Trivy Server response has a "results" array
            if (root.TryGetProperty("results", out var results))
            {
                foreach (var result in results.EnumerateArray())
                {
                    if (result.TryGetProperty("vulnerabilities", out var vulns))
                    {
                        foreach (var vuln in vulns.EnumerateArray())
                        {
                            vulnerabilities.Add(ParseVulnerability(vuln));
                        }
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
                        {
                            vulnerabilities.Add(ParseVulnerability(vuln));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Trivy Server response");
        }

        return vulnerabilities;
    }

    /// <summary>
    /// Parse Trivy CLI JSON output into Vulnerability objects.
    /// </summary>
    private List<Vulnerability> ParseTrivyCliResponse(string jsonResponse)
    {
        var vulnerabilities = new List<Vulnerability>();

        try
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            var root = doc.RootElement;

            // CLI output has a "Results" array
            if (root.TryGetProperty("Results", out var results))
            {
                foreach (var result in results.EnumerateArray())
                {
                    if (result.TryGetProperty("Vulnerabilities", out var vulns))
                    {
                        foreach (var vuln in vulns.EnumerateArray())
                        {
                            vulnerabilities.Add(ParseVulnerability(vuln));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Trivy CLI response");
        }

        return vulnerabilities;
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

        return new Vulnerability
        {
            Id = Guid.NewGuid(),
            CveId = string.IsNullOrEmpty(cveId) ? "unknown" : cveId,
            Package = GetStringProperty(vuln, "PkgName", "pkg_name"),
            Severity = string.IsNullOrEmpty(severity) ? "UNKNOWN" : severity,
            InstalledVersion = GetStringProperty(vuln, "InstalledVersion", "installed_version"),
            FixedVersion = GetStringProperty(vuln, "FixedVersion", "fixed_version"),
            Description = GetStringProperty(vuln, "Title", "title"),
            PublishedDate = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
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