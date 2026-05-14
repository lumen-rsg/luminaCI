using System.Net.Http.Json;
using System.Text.Json;
using Lumina.ScannerService.Data;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.ScannerService.Services;

public class TrivyScannerService
{
    private readonly ScannerDbContext _db;
    private readonly ILogger<TrivyScannerService> _logger;
    private readonly IConfiguration _config;
    private readonly HttpClient _httpClient;

    public TrivyScannerService(ScannerDbContext db, ILogger<TrivyScannerService> logger, IConfiguration config, HttpClient httpClient)
    {
        _db = db;
        _logger = logger;
        _config = config;
        _httpClient = httpClient;

        var trivyEndpoint = _config["Trivy:Endpoint"] ?? "http://trivy:8080";
        _httpClient.BaseAddress = new Uri(trivyEndpoint.EndsWith('/') ? trivyEndpoint : trivyEndpoint + "/");
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
    }

    public async Task<CveReport> ScanArtifactAsync(Guid artifactId, string artifactPath, string scannerType = "Trivy")
    {
        var report = new CveReport
        {
            Id = Guid.NewGuid(),
            ArtifactId = artifactId,
            ScannerType = scannerType,
            Status = ScanStatus.Running,
            ScannedAt = DateTime.UtcNow
        };

        _db.CveReports.Add(report);
        await _db.SaveChangesAsync();

        // Fire-and-forget with proper error handling
        _ = Task.Run(async () =>
        {
            try
            {
                await RunScanViaApiAsync(report, artifactPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in fire-and-forget scan for artifact {ArtifactId}", artifactId);
            }
        });

        return report;
    }

    private async Task RunScanViaApiAsync(CveReport report, string artifactPath)
    {
        try
        {
            // Use Trivy Server REST API instead of CLI to avoid command injection
            // Validate artifactPath — must be a valid image reference
            if (string.IsNullOrWhiteSpace(artifactPath))
            {
                _logger.LogWarning("Empty artifact path provided for scan {ScanId}", report.Id);
                report.Status = ScanStatus.Failed;
                _db.CveReports.Update(report);
                await _db.SaveChangesAsync();
                return;
            }

            // Sanitize: reject shell metacharacters
            if (ContainsDangerousChars(artifactPath))
            {
                _logger.LogError("Artifact path contains dangerous characters: {Path}", artifactPath);
                report.Status = ScanStatus.Failed;
                _db.CveReports.Update(report);
                await _db.SaveChangesAsync();
                return;
            }

            // Call Trivy Server API: POST /api/v1/scans
            var scanRequest = new
            {
                target = artifactPath,
                scanners = new[] { "vuln" }
            };

            var response = await _httpClient.PostAsJsonAsync("api/v1/scans", scanRequest);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Trivy API returned {StatusCode}: {Error}", response.StatusCode, errorBody);
                report.Status = ScanStatus.Failed;
                _db.CveReports.Update(report);
                await _db.SaveChangesAsync();
                return;
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var vulnerabilities = ParseTrivyOutput(jsonResponse, report.Id);

            // Save individual vulnerability records
            foreach (var vuln in vulnerabilities)
            {
                _db.Vulnerabilities.Add(vuln);
            }

            report.Vulnerabilities = vulnerabilities;
            report.CriticalCount = vulnerabilities.Count(v => v.Severity.Equals("CRITICAL", StringComparison.OrdinalIgnoreCase));
            report.HighCount = vulnerabilities.Count(v => v.Severity.Equals("HIGH", StringComparison.OrdinalIgnoreCase));
            report.MediumCount = vulnerabilities.Count(v => v.Severity.Equals("MEDIUM", StringComparison.OrdinalIgnoreCase));
            report.LowCount = vulnerabilities.Count(v => v.Severity.Equals("LOW", StringComparison.OrdinalIgnoreCase));
            report.Status = ScanStatus.Completed;

            _db.CveReports.Update(report);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Scan completed for artifact {ArtifactId}: {Critical}C/{High}H/{Medium}M/{Low}L",
                report.ArtifactId, report.CriticalCount, report.HighCount, report.MediumCount, report.LowCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed for artifact {ArtifactId}", report.ArtifactId);
            report.Status = ScanStatus.Failed;
            _db.CveReports.Update(report);
            await _db.SaveChangesAsync();
        }
    }

    private bool ContainsDangerousChars(string value)
    {
        var dangerous = new[] { '`', '$', ';', '|', '&', '>', '<', '(', ')', '{', '}', '\n', '\r', '\0' };
        return value.IndexOfAny(dangerous) >= 0;
    }

    private List<Vulnerability> ParseTrivyOutput(string jsonOutput, Guid reportId)
    {
        var vulnerabilities = new List<Vulnerability>();
        try
        {
            using var doc = JsonDocument.Parse(jsonOutput);
            // Trivy API response may have "Results" at root or under "report"
            var resultsElement = doc.RootElement;
            if (resultsElement.TryGetProperty("report", out var reportEl))
                resultsElement = reportEl;
            if (!resultsElement.TryGetProperty("Results", out var results))
                return vulnerabilities;

            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("Vulnerabilities", out var vulns)) continue;
                foreach (var vuln in vulns.EnumerateArray())
                {
                    vulnerabilities.Add(new Vulnerability
                    {
                        Id = Guid.NewGuid().ToString(),
                        CveReportId = reportId,
                        CveId = vuln.TryGetProperty("VulnerabilityID", out var cveId) ? cveId.GetString() ?? "" : "",
                        PackageName = vuln.TryGetProperty("PkgName", out var pkg) ? pkg.GetString() ?? "" : "",
                        Title = vuln.TryGetProperty("Title", out var title) ? title.GetString() ?? "" : "",
                        Description = vuln.TryGetProperty("Description", out var desc) ? desc.GetString() ?? "" : "",
                        Severity = vuln.TryGetProperty("Severity", out var sev) ? sev.GetString() ?? "" : "",
                        FixedVersion = vuln.TryGetProperty("FixedVersion", out var fv) ? fv.GetString() : null,
                        InstalledVersion = vuln.TryGetProperty("InstalledVersion", out var iv) ? iv.GetString() ?? "" : "",
                        PublishedDate = vuln.TryGetProperty("PublishedDate", out var pd) && pd.ValueKind == JsonValueKind.String
                            ? DateTime.Parse(pd.GetString()!) : DateTime.MinValue
                    });
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse trivy output");
        }
        return vulnerabilities;
    }

    public async Task<CveReport?> GetReportAsync(Guid id)
    {
        return await _db.CveReports
            .Include(r => r.Vulnerabilities)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<List<CveReport>> GetArtifactReportsAsync(Guid artifactId)
    {
        return await _db.CveReports
            .Include(r => r.Vulnerabilities)
            .Where(r => r.ArtifactId == artifactId)
            .OrderByDescending(r => r.ScannedAt)
            .ToListAsync();
    }

    public async Task<List<CveReport>> GetRecentReportsAsync(int count = 20)
    {
        return await _db.CveReports
            .Include(r => r.Vulnerabilities)
            .OrderByDescending(r => r.ScannedAt)
            .Take(count)
            .ToListAsync();
    }
}