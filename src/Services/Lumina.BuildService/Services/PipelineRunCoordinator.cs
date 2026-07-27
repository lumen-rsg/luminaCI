using Lumina.BuildService.Data;
using Lumina.Shared.Events;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

/// <summary>
/// Advances one build through the immutable step snapshot captured at trigger
/// time. Only this coordinator starts post-build work, so undeclared stages are
/// never run and later stages cannot overtake earlier ones.
/// </summary>
public sealed class PipelineRunCoordinator
{
    private readonly BuildDbContext _db;
    private readonly IBus _bus;
    private readonly ILogger<PipelineRunCoordinator> _logger;

    public PipelineRunCoordinator(
        BuildDbContext db,
        IBus bus,
        ILogger<PipelineRunCoordinator> logger)
    {
        _db = db;
        _bus = bus;
        _logger = logger;
    }

    public async Task CompleteBuildStepAsync(Guid buildJobId, CancellationToken cancellationToken = default)
    {
        var job = await LoadJobAsync(buildJobId, cancellationToken);
        foreach (var artifact in job.Artifacts)
        {
            await _bus.Publish(new HashStoreRequested(
                artifact.Id,
                artifact.FileName,
                artifact.HashSha256
                    ?? throw new InvalidOperationException($"Artifact {artifact.Id} has no SHA-256 digest."),
                artifact.HashMd5 ?? string.Empty,
                artifact.FileSize,
                DateTime.UtcNow), cancellationToken);
        }

        await CompleteAndAdvanceAsync(job, StepType.Build, cancellationToken);
    }

    public async Task ReportScanAsync(
        CveScanCompleted result,
        CancellationToken cancellationToken = default)
    {
        var job = await LoadJobByArtifactAsync(result.ArtifactId, cancellationToken);
        var scanRun = job.StepRuns.SingleOrDefault(step => step.Type == StepType.Scan);
        if (scanRun is null || scanRun.Status != StepStatus.Running)
        {
            _logger.LogWarning(
                "Ignoring scan result for artifact {ArtifactId}: build {BuildJobId} has no running Scan step",
                result.ArtifactId, job.Id);
            return;
        }

        var artifact = job.Artifacts.Single(item => item.Id == result.ArtifactId);
        artifact.CveScanStatus = result.Status;
        await _db.SaveChangesAsync(cancellationToken);

        if (result.Status != ScanStatus.Completed)
        {
            await FailStepAsync(
                job,
                scanRun,
                $"CVE scan for '{artifact.FileName}' finished with {result.Status}.",
                cancellationToken);
            return;
        }

        if (result.CriticalCount > 0 || result.HighCount > 0 || result.UnknownCount > 0)
        {
            await FailStepAsync(
                job,
                scanRun,
                $"CVE policy rejected '{artifact.FileName}': critical={result.CriticalCount}, high={result.HighCount}, unknown={result.UnknownCount}.",
                cancellationToken);
            return;
        }

        var allScanned = await _db.BuildArtifacts
            .Where(item => item.BuildJobId == job.Id)
            .AllAsync(item => item.CveScanStatus == ScanStatus.Completed, cancellationToken);
        if (allScanned)
        {
            await CompleteAndAdvanceAsync(job, StepType.Scan, cancellationToken);
        }
    }

    public async Task ReportSignedAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var job = await LoadJobByArtifactAsync(artifactId, cancellationToken);
        var signRun = job.StepRuns.SingleOrDefault(step => step.Type == StepType.Sign);
        if (signRun is null || signRun.Status != StepStatus.Running)
        {
            _logger.LogWarning(
                "Ignoring signing result for artifact {ArtifactId}: build {BuildJobId} has no running Sign step",
                artifactId, job.Id);
            return;
        }

        var allSigned = await _db.BuildArtifacts
            .Where(item => item.BuildJobId == job.Id)
            .AllAsync(item => item.SigningKeyFingerprint != null && item.SignedAt != null, cancellationToken);
        if (allSigned)
        {
            await CompleteAndAdvanceAsync(job, StepType.Sign, cancellationToken);
        }
    }

    public async Task ReportPublishedAsync(
        PackagePublished result,
        CancellationToken cancellationToken = default)
    {
        var job = await LoadJobByArtifactAsync(result.ArtifactId, cancellationToken);
        var publishRun = job.StepRuns.SingleOrDefault(step => step.Type == StepType.Publish);
        if (publishRun is null || publishRun.Status != StepStatus.Running)
        {
            _logger.LogWarning(
                "Ignoring publication result for artifact {ArtifactId}: build {BuildJobId} has no running Publish step",
                result.ArtifactId, job.Id);
            return;
        }

        var artifact = job.Artifacts.Single(item => item.Id == result.ArtifactId);
        artifact.PublishedRepositoryId = result.RepositoryId;
        artifact.PublishedAt = result.PublishedAt;
        await _db.SaveChangesAsync(cancellationToken);

        var allPublished = await _db.BuildArtifacts
            .Where(item => item.BuildJobId == job.Id)
            .AllAsync(
                item => item.PublishedRepositoryId == result.RepositoryId && item.PublishedAt != null,
                cancellationToken);
        if (allPublished)
        {
            await CompleteAndAdvanceAsync(job, StepType.Publish, cancellationToken);
        }
    }

    public async Task FailStepAsync(
        Guid buildJobId,
        StepType type,
        string error,
        CancellationToken cancellationToken = default)
    {
        var job = await LoadJobAsync(buildJobId, cancellationToken);
        var step = job.StepRuns.Single(item => item.Type == type);
        await FailStepAsync(job, step, error, cancellationToken);
    }

    private async Task CompleteAndAdvanceAsync(
        BuildJob job,
        StepType completedType,
        CancellationToken cancellationToken)
    {
        var completed = job.StepRuns.Single(step => step.Type == completedType);
        if (completed.Status == StepStatus.Success)
        {
            return;
        }

        completed.Status = StepStatus.Success;
        completed.CompletedAt = DateTime.UtcNow;

        var next = job.StepRuns
            .Where(step => step.Order > completed.Order && step.Status == StepStatus.Pending)
            .OrderBy(step => step.Order)
            .FirstOrDefault();

        if (next is null)
        {
            job.Status = BuildStatus.Success;
            job.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Pipeline run {BuildJobId} completed successfully", job.Id);
            return;
        }

        next.Status = StepStatus.Running;
        next.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            await DispatchStepAsync(job, next, cancellationToken);
        }
        catch (Exception ex)
        {
            await FailStepAsync(job, next, ex.Message, cancellationToken);
        }
    }

    private async Task DispatchStepAsync(
        BuildJob job,
        BuildStepRun step,
        CancellationToken cancellationToken)
    {
        switch (step.Type)
        {
            case StepType.Scan:
                foreach (var artifact in job.Artifacts)
                {
                    await _bus.Publish(new CveScanRequested(
                        artifact.Id,
                        artifact.FilePath,
                        artifact.FileName,
                        "Trivy",
                        DateTime.UtcNow), cancellationToken);
                }
                break;

            case StepType.Sign:
                var key = await _bus.Request<GetActiveSigningKey, ActiveSigningKey>(
                    new GetActiveSigningKey(),
                    cancellationToken,
                    timeout: TimeSpan.FromSeconds(10));
                var keyId = key.Message.KeyId
                    ?? throw new InvalidOperationException("No active PGP signing key is available.");

                foreach (var artifact in job.Artifacts)
                {
                    await _bus.Publish(new PackageSigningRequested(
                        artifact.Id,
                        artifact.FilePath,
                        artifact.FileName,
                        artifact.HashSha256
                            ?? throw new InvalidOperationException($"Artifact {artifact.Id} has no SHA-256 digest."),
                        keyId,
                        DateTime.UtcNow), cancellationToken);
                }
                break;

            case StepType.Publish:
                var repositoryId = Guid.Parse(step.Configuration["repositoryId"]);
                foreach (var artifact in job.Artifacts)
                {
                    await _bus.Publish(new PackagePublishRequested(
                        artifact.Id,
                        repositoryId,
                        job.TriggeredBy,
                        DateTime.UtcNow), cancellationToken);
                }
                break;

            default:
                throw new InvalidOperationException(
                    $"Step {step.Type} cannot be dispatched after a build.");
        }

        _logger.LogInformation(
            "Started {StepType} step {StepRunId} for build {BuildJobId}",
            step.Type, step.Id, job.Id);
    }

    private async Task FailStepAsync(
        BuildJob job,
        BuildStepRun step,
        string error,
        CancellationToken cancellationToken)
    {
        if (step.Status == StepStatus.Failed)
        {
            return;
        }

        step.Status = StepStatus.Failed;
        step.Error = error.Length > 2048 ? error[..2048] : error;
        step.CompletedAt = DateTime.UtcNow;
        foreach (var pending in job.StepRuns.Where(item => item.Status == StepStatus.Pending))
        {
            pending.Status = StepStatus.Skipped;
            pending.CompletedAt = DateTime.UtcNow;
        }

        job.Status = BuildStatus.Failed;
        job.CompletedAt = DateTime.UtcNow;
        var entry = $"[{DateTime.UtcNow:O}] {step.Type.ToString().ToUpperInvariant()} FAILED: {step.Error}";
        job.Logs = string.IsNullOrWhiteSpace(job.Logs) ? entry : $"{job.Logs}\n{entry}";
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogError(
            "Pipeline run {BuildJobId} failed at {StepType}: {Error}",
            job.Id, step.Type, step.Error);
    }

    private async Task<BuildJob> LoadJobAsync(Guid buildJobId, CancellationToken cancellationToken) =>
        await _db.BuildJobs
            .Include(job => job.Artifacts)
            .Include(job => job.StepRuns)
            .SingleAsync(job => job.Id == buildJobId, cancellationToken);

    private async Task<BuildJob> LoadJobByArtifactAsync(Guid artifactId, CancellationToken cancellationToken) =>
        await _db.BuildJobs
            .Include(job => job.Artifacts)
            .Include(job => job.StepRuns)
            .SingleAsync(job => job.Artifacts.Any(artifact => artifact.Id == artifactId), cancellationToken);
}
