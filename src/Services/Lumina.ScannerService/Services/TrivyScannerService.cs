using System.Diagnostics;
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

    public TrivyScannerService(ScannerDbContext db, ILogger<TrivyScannerService> logger, IConfiguration config)
    {
        _db = db;
        _logger = logger;
        _config = config;
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

        _ = RunScanAsync(report, artifactPath);

        return report;
    }

    private async Task RunScanAsync(CveReport report, string artifactPath)
    {
        try
        {
            var trivyPath = _config["Trivy:Path"] ?? "trivy";
            var startInfo = new ProcessStartInfo
            {
                FileName = trivyPath,
                Arguments = $"--format json --quiet image {artifactPath}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) throw new InvalidOperationException("Failed to start trivy");

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 && process.ExitCode != 1)
            {
                report.Status = ScanStatus.Failed;
                _db.CveReports.Update(report);
                await _db.SaveChangesAsync();
                return;
            }

            var vulnerabilities = ParseTrivyOutput(output, report.Id);
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

    private List<Vulnerability> ParseTrivyOutput(string jsonOutput, Guid reportId)
    {
        var vulnerabilities = new List<Vulnerability>();
        try
        {
            using var doc = JsonDocument.Parse(jsonOutput);
            var results = doc.RootElement.GetProperty("Results");
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