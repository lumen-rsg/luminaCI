using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.Events;
using Lumina.Shared.Models.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Consumers;

public class CveScanCompletedConsumer : IConsumer<CveScanCompleted>
{
    private readonly BuildDbContext _db;
    private readonly PipelineEngine _pipelineEngine;
    private readonly ILogger<CveScanCompletedConsumer> _logger;
    private readonly IBus _bus;

    public CveScanCompletedConsumer(
        BuildDbContext db,
        PipelineEngine pipelineEngine,
        ILogger<CveScanCompletedConsumer> logger,
        IBus bus)
    {
        _db = db;
        _pipelineEngine = pipelineEngine;
        _logger = logger;
        _bus = bus;
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

            // Fail-closed for a non-Completed scan (Failed/Error/Running). A Failed
            // scan used to fall through here with no signing request and no build
            // status change, leaving the artifact "successfully built but never
            // signed" — and because the publish gate only blocks *unsigned*
            // artifacts, a transient scan/parse failure would leave it dangling
            // forever. Mark the build Failed so it can never be published and an
            // operator must re-run it. (This pairs with ScannerService now marking
            // a parse failure Status=Failed instead of "0 vulnerabilities".)
            if (msg.Status != ScanStatus.Completed)
            {
                _logger.LogError("CVE scan did not complete (Status={Status}) for artifact {ArtifactId} — failing build {JobId}: result is not trustworthy enough to sign",
                    msg.Status, msg.ArtifactId, job.Id);

                job.Status = BuildStatus.Failed;
                job.CompletedAt = DateTime.UtcNow;
                var failMsg = $"[{DateTime.UtcNow:O}] PUBLISH BLOCKED: CVE scan finished with Status={msg.Status} for artifact '{artifact.FileName}'. The artifact cannot be signed or published until the scan completes cleanly. Re-run the build.";
                job.Logs = string.IsNullOrEmpty(job.Logs) ? failMsg : $"{job.Logs}\n{failMsg}";
                _db.Update(job);
                await _db.SaveChangesAsync();
                return;
            }

            // Only request PGP signing if CVE scan passed with no critical/high
            // vulnerabilities AND no unclassified-severity ones. UnknownCount is
            // treated conservatively: a vuln we couldn't classify must never
            // silently pass the gate (it may well be critical/high under a label
            // we didn't recognize).
            if (msg.CriticalCount == 0 && msg.HighCount == 0 && msg.UnknownCount == 0)
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
                    // Fail-closed: an artifact that passed its CVE scan but cannot be
                    // signed must NOT silently degrade to an unsigned "success" — that
                    // would let an unsigned RPM reach publication. Mark the build
                    // Failed so the publish gate (and the UI) treat it correctly.
                    // Operators generate a PGP key in Security settings to recover.
                    _logger.LogError("No active PGP key found — failing build {JobId} for artifact {ArtifactId}: unsigned artifacts cannot be published",
                        job.Id, msg.ArtifactId);

                    job.Status = BuildStatus.Failed;
                    job.CompletedAt = DateTime.UtcNow;
                    var failMsg = $"[{DateTime.UtcNow:O}] PUBLISH BLOCKED: no active PGP key — artifact '{artifact.FileName}' cannot be signed. Generate a PGP key in Security settings and re-run the build.";
                    job.Logs = string.IsNullOrEmpty(job.Logs) ? failMsg : $"{job.Logs}\n{failMsg}";
                    artifact.CveScanStatus = msg.Status;
                    _db.Update(job);
                    await _db.SaveChangesAsync();
                }
            }
            else
            {
                _logger.LogWarning("CVE scan found {Critical} critical, {High} high, {Unknown} unknown-severity vulnerabilities for artifact {ArtifactId} — skipping PGP signing",
                    msg.CriticalCount, msg.HighCount, msg.UnknownCount, msg.ArtifactId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing CveScanCompleted for artifact {ArtifactId}", msg.ArtifactId);
            throw; // Re-throw so MassTransit retries
        }
    }

    /// <summary>
    /// Look up the active PGP key via the message bus (request/response) instead
    /// of a direct HTTP call. The previous <c>GET /api/security/keys</c> request
    /// carried no JWT and would be rejected now that SecurityController is gated
    /// by <c>[Authorize]</c>; the bus keeps internal service-to-service traffic
    /// off the HTTP surface entirely.
    /// </summary>
    private async Task<Guid?> GetActivePgpKeyIdAsync()
    {
        try
        {
            var response = await _bus.Request<GetActiveSigningKey, ActiveSigningKey>(
                new GetActiveSigningKey(), timeout: TimeSpan.FromSeconds(10));

            return response.Message.KeyId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get active PGP key from SecurityService via bus");
            return null;
        }
    }
}
