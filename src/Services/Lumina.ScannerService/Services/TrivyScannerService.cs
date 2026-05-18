using System.Diagnostics;
using System.Text.Json;
using Lumina.ScannerService.Data;
using Lumina.Shared.DTOs;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.ScannerService.Services;

public class TrivyScannerService
{
    private readonly ILogger<TrivyScannerService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _trivyPath;
    private readonly HttpClient _httpClient;

    public TrivyScannerService(ILogger<TrivyScannerService> logger, IConfiguration config, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;
        _trivyPath = _config["Trivy:Path"] ?? "trivy";
        var buildServiceUrl = config["Services:BuildService"] ?? "http://build-service:5001";
        _httpClient = new HttpClient { BaseAddress = new Uri(buildServiceUrl) };
    }

    public async Task<CveReport> ScanArtifactAsync(Guid artifactId, string artifactPath, string scannerType = "Trivy")
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScannerDbContext>();

        var report = new CveReport
        {
            Id = Guid.NewGuid(),
            ArtifactId = artifactId,
            ScannerType = scannerType,
            Status = ScanStatus.Running,
            ScannedAt = DateTime.UtcNow
        };

        db.CveReports.Add(report);
        await db.SaveChangesAsync();

        var reportId = report.Id;

        // Fire-and-forget with proper error handling
        _ = Task.Run(async () =>
        {
            try
            {
                await RunScanViaCliAsync(reportId, artifactPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in fire-and-forget scan for artifact {ArtifactId}", artifactId);
            }
        });

        return report;
    }

    private async Task RunScanViaCliAsync(Guid reportId, string artifactPath)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScannerDbContext>();

        try
        {
            var report = await db.CveReports.FindAsync(reportId);
            if (report == null)
            {
                _logger.LogError("Report {ReportId} not found", reportId);
                return;
            }

            if (string.IsNullOrWhiteSpace(artifactPath))
            {
                _logger.LogWarning("Empty artifact path provided for scan {ScanId}", reportId);
                report.Status = ScanStatus.Failed;
                db.CveReports.Update(report);
                await db.SaveChangesAsync();
                return;
            }

            if (!File.Exists(artifactPath))
            {
                _logger.LogError("Artifact file not found: {Path}", artifactPath);
                report.Status = ScanStatus.Failed;
                db.CveReports.Update(report);
                await db.SaveChangesAsync();
                return;
            }

            _logger.LogInformation("Starting trivy CLI scan for {Path}", artifactPath);

            // For RPM files: extract to temp dir and scan with rootfs to detect OS package vulns.
            // For other files: scan directory/source with fs scanner.
            var isRpm = artifactPath.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase);
            string scanPath = artifactPath;

            if (isRpm)
            {
                // Extract RPM to a temp directory for rootfs scanning
                var tmpDir = Path.Combine(Path.GetTempPath(), $"trivy-rpm-{Guid.NewGuid():N}");
                Directory.CreateDirectory(tmpDir);

                var extractPsi = new ProcessStartInfo
                {
                    FileName = "bash",
                    Arguments = $"-c \"rpm2cpio '{artifactPath}' | cpio -idm -D '{tmpDir}' 2>/dev/null\"",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var extractProcess = Process.Start(extractPsi);
                if (extractProcess != null)
                {
                    await extractProcess.StandardError.ReadToEndAsync();
                    await extractProcess.WaitForExitAsync();
                }

                scanPath = tmpDir;
                _logger.LogInformation("Extracted RPM to {TmpDir} for scanning", tmpDir);
            }

            var psi = new ProcessStartInfo
            {
                FileName = _trivyPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add(isRpm ? "rootfs" : "fs");
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add("--scanners");
            psi.ArgumentList.Add("vuln");
            psi.ArgumentList.Add(scanPath);

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            {
                _logger.LogError("Trivy CLI failed with exit code {ExitCode}: {Error}", process.ExitCode, stderr);
                report.Status = ScanStatus.Failed;
                db.CveReports.Update(report);
                await db.SaveChangesAsync();
                return;
            }

            _logger.LogDebug("Trivy CLI stderr: {Stderr}", stderr);

            var vulnerabilities = ParseTrivyOutput(stdout, report.Id);

            // Save individual vulnerability records
            foreach (var vuln in vulnerabilities)
            {
                db.Vulnerabilities.Add(vuln);
            }

            report.Vulnerabilities = vulnerabilities;
            report.CriticalCount = vulnerabilities.Count(v => v.Severity.Equals("CRITICAL", StringComparison.OrdinalIgnoreCase));
            report.HighCount = vulnerabilities.Count(v => v.Severity.Equals("HIGH", StringComparison.OrdinalIgnoreCase));
            report.MediumCount = vulnerabilities.Count(v => v.Severity.Equals("MEDIUM", StringComparison.OrdinalIgnoreCase));
            report.LowCount = vulnerabilities.Count(v => v.Severity.Equals("LOW", StringComparison.OrdinalIgnoreCase));
            report.Status = ScanStatus.Completed;

            db.CveReports.Update(report);
            await db.SaveChangesAsync();

            _logger.LogInformation("Scan completed for artifact {ArtifactId}: {Critical}C/{High}H/{Medium}M/{Low}L",
                report.ArtifactId, report.CriticalCount, report.HighCount, report.MediumCount, report.LowCount);

            // Notify build service about scan completion
            await NotifyBuildServiceAsync(report.ArtifactId, report.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed for report {ReportId}", reportId);
            try
            {
                var report = await db.CveReports.FindAsync(reportId);
                if (report != null)
                {
                    report.Status = ScanStatus.Failed;
                    db.CveReports.Update(report);
                    await db.SaveChangesAsync();

                    // Notify build service about scan failure
                    await NotifyBuildServiceAsync(report.ArtifactId, ScanStatus.Failed);
                }
            }
            catch { /* best effort */ }
        }
    }

    private async Task NotifyBuildServiceAsync(Guid artifactId, ScanStatus status)
    {
        try
        {
            var statusStr = status switch
            {
                ScanStatus.Completed => "Completed",
                ScanStatus.Failed => "Failed",
                _ => "Unknown"
            };
            var response = await _httpClient.PutAsync(
                $"/api/builds/artifacts/{artifactId}/scan-status?status={statusStr}", null);
            if (response.IsSuccessStatusCode)
                _logger.LogInformation("Notified build service: artifact {ArtifactId} scan {Status}", artifactId, statusStr);
            else
                _logger.LogWarning("Failed to notify build service: {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not notify build service about scan status for artifact {ArtifactId}", artifactId);
        }
    }

    private List<Vulnerability> ParseTrivyOutput(string jsonOutput, Guid reportId)
    {
        var vulnerabilities = new List<Vulnerability>();
        try
        {
            using var doc = JsonDocument.Parse(jsonOutput);
            var resultsElement = doc.RootElement;
            if (resultsElement.TryGetProperty("Results", out var results))
            {
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
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse trivy output");
        }
        return vulnerabilities;
    }

    public async Task<CveReport?> GetReportAsync(Guid id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScannerDbContext>();
        return await db.CveReports
            .Include(r => r.Vulnerabilities)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<List<CveReport>> GetArtifactReportsAsync(Guid artifactId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScannerDbContext>();
        return await db.CveReports
            .Include(r => r.Vulnerabilities)
            .Where(r => r.ArtifactId == artifactId)
            .OrderByDescending(r => r.ScannedAt)
            .ToListAsync();
    }

    public async Task<List<CveReport>> GetRecentReportsAsync(int count = 20)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScannerDbContext>();
        return await db.CveReports
            .Include(r => r.Vulnerabilities)
            .OrderByDescending(r => r.ScannedAt)
            .Take(count)
            .ToListAsync();
    }

    public async Task<ScanListResponse> GetScansPaginatedAsync(int page = 1, int pageSize = 20)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScannerDbContext>();

        var totalCount = await db.CveReports.CountAsync();
        var scans = await db.CveReports
            .OrderByDescending(r => r.ScannedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new ScanResponse(r.Id, r.ArtifactId, r.ScannerType, r.Status, r.ScannedAt, null))
            .ToListAsync();

        return new ScanListResponse(scans, totalCount, page, pageSize);
    }
}
