using Lumina.BuildService.Data;
using Lumina.BuildService.Services.PackageGraph;
using Lumina.Shared.Events;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

internal sealed class NativePromotionGateHostedService(
    IServiceScopeFactory scopeFactory,
    INativePromotionGateResourceClient resources,
    IConfiguration configuration,
    BuildExecutorSelection executorSelection,
    CandidatePromotionSelection candidatePromotion,
    ILogger<NativePromotionGateHostedService> logger) : BackgroundService
{
    private const int MaximumLogCharacters = 1_000_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            await ReconcileAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await using var queryScope = scopeFactory.CreateAsyncScope();
        var queryDb = queryScope.ServiceProvider.GetRequiredService<BuildDbContext>();
        var statuses = candidatePromotion.Enabled
            ? new[] { NativePromotionGateStatus.Pending, NativePromotionGateStatus.Running }
            : new[] { NativePromotionGateStatus.Running };
        var ids = await queryDb.NativePromotionGates.AsNoTracking()
            .Where(item => statuses.Contains(item.Status))
            .OrderBy(item => item.CreatedAt)
            .Select(item => item.Id)
            .Take(16)
            .ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            try
            {
                await ReconcileOneAsync(id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Native promotion gate reconciliation failed for {PromotionSetId}", id);
            }
        }
    }

    private async Task ReconcileOneAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();
        var gate = await db.NativePromotionGates.SingleOrDefaultAsync(
            item => item.Id == id, cancellationToken);
        if (gate is null || gate.Status is NativePromotionGateStatus.Passed or NativePromotionGateStatus.Failed)
            return;

        var runner = ResolveRunner(gate);
        var limits = KubernetesBuildPolicy.ResolveLimits(configuration);
        var network = KubernetesBuildPolicy.ResolveNetworkPolicy(configuration);
        if (gate.Status == NativePromotionGateStatus.Pending)
        {
            if (!candidatePromotion.Enabled || executorSelection.Backend != BuildExecutorBackend.Kubernetes ||
                string.IsNullOrWhiteSpace(executorSelection.KubernetesNamespace))
                return;
            if (gate.BundlePreparedAt is null)
            {
                if (gate.PreparationRequestedAt is null)
                {
                    var manifest = NativePromotionGateManifestPolicy.Read(gate);
                    var preparationRequestedAt = DateTime.UtcNow;
                    var preparationPublisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
                    await preparationPublisher.Publish(new PromotionGatePreparationRequested(
                        gate.Id,
                        gate.RepositoryId,
                        gate.CandidateManifestSha256,
                        manifest.Candidates.Select(item => new PromotionGateCandidateInput(
                            item.ArtifactId, item.CandidatePackageId, item.ProjectPackageId,
                            item.FileName, item.ObjectName, item.Size, item.Sha256)).ToList(),
                        preparationRequestedAt), cancellationToken);
                    gate.PreparationRequestedAt = preparationRequestedAt;
                    gate.UpdatedAt = preparationRequestedAt;
                    await db.SaveChangesAsync(cancellationToken);
                    logger.LogInformation(
                        "Requested immutable repository bundle for promotion set {PromotionSetId}", gate.Id);
                }
                return;
            }
            var identity = await resources.EnsureCreatedAsync(
                executorSelection.KubernetesNamespace,
                NativePromotionGateJobFactory.Create(gate, runner, limits, network),
                NativePromotionGateJobFactory.CreateNetworkPolicy(gate, network),
                cancellationToken);
            var now = DateTime.UtcNow;
            gate.Status = NativePromotionGateStatus.Running;
            gate.KubernetesNamespace = identity.Namespace;
            gate.KubernetesJobName = identity.JobName;
            gate.KubernetesJobUid = identity.JobUid;
            gate.StartedAt = now;
            gate.UpdatedAt = now;
            var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
            await publish.Publish(new PromotionGateStarted(
                gate.Id, gate.RepositoryId, identity.JobName, identity.JobUid, now), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Created native promotion gate Job {Namespace}/{JobName} ({JobUid})",
                identity.Namespace, identity.JobName, identity.JobUid);
            return;
        }

        var currentIdentity = new KubernetesBuildResourceIdentity(
            gate.KubernetesNamespace!, gate.KubernetesJobName!, gate.KubernetesJobUid!, gate.KubernetesPodName);
        var signer = scope.ServiceProvider.GetRequiredService<IKubernetesObjectUrlSigner>();
        var transport = await NativePromotionGateTransportPolicy.CreateAsync(
            gate, currentIdentity, limits, signer, DateTimeOffset.UtcNow, cancellationToken);
        await resources.ActivateAsync(
            currentIdentity,
            NativePromotionGateTransportPolicy.CreateSecret(
                currentIdentity.Namespace, currentIdentity.JobName, transport),
            gate,
            cancellationToken);
        var observation = await resources.ObserveAsync(currentIdentity, cancellationToken);
        currentIdentity = currentIdentity with { PodName = observation.PodName ?? currentIdentity.PodName };
        string? logs = null;
        if (!string.IsNullOrWhiteSpace(currentIdentity.PodName))
            logs = await resources.ReadLogsAsync(currentIdentity, MaximumLogCharacters, cancellationToken);
        gate.KubernetesPodName = currentIdentity.PodName;
        if (logs != null)
            gate.Logs = logs;
        gate.UpdatedAt = DateTime.UtcNow;

        if (observation.Phase is not (KubernetesBuildPhase.Succeeded or KubernetesBuildPhase.Failed))
        {
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var succeeded = observation.Phase == KubernetesBuildPhase.Succeeded;
        string? resultSha256 = null;
        string? failure = null;
        if (succeeded)
        {
            try
            {
                resultSha256 = NativePromotionGateResultPolicy.ValidateAndHash(
                    gate, currentIdentity, logs ?? string.Empty);
            }
            catch (Exception exception)
            {
                succeeded = false;
                failure = $"Native DNF gate result rejected: {exception.Message}";
            }
        }
        else
        {
            failure = $"Native DNF gate Job failed: {observation.Reason ?? "unknown reason"}.";
        }
        failure = failure is { Length: > 2048 } ? failure[..2048] : failure;
        var completedAt = DateTime.UtcNow;
        gate.Status = succeeded ? NativePromotionGateStatus.Passed : NativePromotionGateStatus.Failed;
        gate.ResultSha256 = resultSha256;
        gate.FailureReason = failure;
        gate.CompletedAt = completedAt;
        gate.UpdatedAt = completedAt;
        if (!succeeded)
        {
            var failures = scope.ServiceProvider.GetRequiredService<ProjectDeliveryFailureService>();
            await failures.FailAsync(
                gate.ProjectWebhookDeliveryId,
                "promotion-gate-failed",
                cancellationToken,
                saveChanges: false);
        }
        var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        await publisher.Publish(new PromotionGateCompleted(
            gate.Id, gate.RepositoryId, currentIdentity.JobName, currentIdentity.JobUid,
            gate.RunnerImageDigest, succeeded, resultSha256, failure, completedAt), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Native promotion gate {PromotionSetId} completed with {Outcome}",
            gate.Id, succeeded ? "success" : "failure");
    }

    private KubernetesRunner ResolveRunner(NativePromotionGate gate)
    {
        var profile = gate.TargetArchitecture switch
        {
            "x86_64" => "fedora-44-x86_64",
            "aarch64" => "fedora-44-aarch64",
            _ => throw new InvalidOperationException("Native promotion gate architecture is unsupported.")
        };
        var runner = KubernetesBuildPolicy.ResolveRunner(configuration, new BuildJob
        {
            BuildProfile = profile,
            TargetDistribution = "fedora",
            TargetRelease = "44",
            TargetArchitecture = gate.TargetArchitecture
        }, requestedImage: null);
        if (!runner.Image.EndsWith($"@{gate.RunnerImageDigest}", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Native promotion gate runner digest no longer matches administrator policy.");
        return runner;
    }
}
