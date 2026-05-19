using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.Events;
using Lumina.Shared.Models.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Lumina.BuildService.Consumers;

public class CveScanCompletedConsumer : IConsumer<CveScanCompleted>
{
    private readonly BuildDbContext _db;
    private readonly PipelineEngine _pipelineEngine;
    private readonly ILogger<CveScanCompletedConsumer> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public CveScanCompletedConsumer(
        BuildDbContext db,
        PipelineEngine pipelineEngine,
        ILogger<CveScanCompletedConsumer> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration config)
    {
        _db = db;
        _pipelineEngine = pipelineEngine;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    public async Task Consume(ConsumeContext<CveScanCompleted> context)
    {
        var msg = context.Message;
        _logger.LogInformation("Received CveScanCompleted for artifact {ArtifactId}: Status={Status}, Critical={Critical}, High={High}",
            msg.ArtifactId, msg.Status, msg.CriticalCount, msg.HighCount);

        try
        {
            var job = await _db.BuildJobs.Include(b => b.Artifacts)
                .FirstOrDefaultAsync(b => b.Artifacts.Any(a => a.Id == msg.ArtifactId));

            if (job == null)
            {
                _logger.LogWarning("No build job found for artifact {ArtifactId}", msg.ArtifactId);
                return;
            }

            var artifact = job.Artifacts.First(a => a.Id == msg.ArtifactId);
            artifact.CveScanStatus = msg.Status;
            _db.Update(artifact);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Updated CVE scan status for artifact {ArtifactId} to {Status}", msg.ArtifactId, msg.Status);

            // Only request PGP signing if CVE scan passed with no critical/high vulnerabilities
            if (msg.Status == ScanStatus.Completed && msg.CriticalCount == 0 && msg.HighCount == 0)
            {
                var activeKeyId = await GetActivePgpKeyIdAsync();
                if (activeKeyId.HasValue)
                {
                    await context.Publish(new PackageSigningRequested(
                        msg.ArtifactId, artifact.FilePath ?? "", artifact.FileName, activeKeyId.Value, DateTime.UtcNow));
                    _logger.LogInformation("Published PackageSigningRequested for artifact {ArtifactId} with key {KeyId}", msg.ArtifactId, activeKeyId.Value);
                }
                else
                {
                    _logger.LogWarning("No active PGP key found — skipping signing for artifact {ArtifactId}. Create a PGP key in Security settings.", msg.ArtifactId);
                }
            }
            else if (msg.Status == ScanStatus.Completed)
            {
                _logger.LogWarning("CVE scan found {Critical} critical and {High} high vulnerabilities for artifact {ArtifactId} — skipping PGP signing",
                    msg.CriticalCount, msg.HighCount, msg.ArtifactId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing CveScanCompleted for artifact {ArtifactId}", msg.ArtifactId);
            throw; // Re-throw so MassTransit retries
        }
    }

    /// <summary>
    /// Get the active PGP key ID from SecurityService.
    /// </summary>
    private async Task<Guid?> GetActivePgpKeyIdAsync()
    {
        try
        {
            var securityServiceUrl = _config["Services:SecurityService"] ?? "http://security-service:5002";
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(securityServiceUrl);
            client.Timeout = TimeSpan.FromSeconds(10);

            var response = await client.GetAsync("/api/security/keys");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("SecurityService keys endpoint returned {StatusCode}", response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);

            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

            // Handle both array and single object responses
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var key in data.EnumerateArray())
                {
                    if (key.TryGetProperty("isActive", out var isActive) && isActive.GetBoolean())
                    {
                        return key.GetProperty("id").GetGuid();
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get PGP keys from SecurityService");
            return null;
        }
    }
}
