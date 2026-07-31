using System.Text.Json;
using Lumina.BuildService.Data;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services.PackageGraph;

public sealed class ProjectDispatchService(
    BuildDbContext db,
    IProjectBuildTrigger builds,
    ILogger<ProjectDispatchService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task AdvanceAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        var delivery = await db.ProjectWebhookDeliveries
            .Include(item => item.BuildProject)
            .ThenInclude(project => project.Pipelines)
            .Include(item => item.BuildJobs)
            .ThenInclude(job => job.Artifacts)
            .SingleOrDefaultAsync(item => item.Id == deliveryId, cancellationToken);
        if (delivery is null || delivery.Status is ProjectWebhookStatus.Completed or
            ProjectWebhookStatus.Ignored or ProjectWebhookStatus.Failed)
            return;
        if (delivery.Status is not (ProjectWebhookStatus.PlanReady or ProjectWebhookStatus.Dispatched))
            return;

        try
        {
            var plan = DeserializeAndValidate(delivery);
            ValidateCurrentBindings(delivery, plan);
            var targetsByPackage = plan.Stages
                .SelectMany(stage => stage.Targets.Select(target => (stage.Order, Target: target)))
                .ToDictionary(item => item.Target.PackageId, StringComparer.Ordinal);
            ValidateExistingJobs(delivery.BuildJobs, targetsByPackage);

            foreach (var stage in plan.Stages)
            {
                var jobs = delivery.BuildJobs
                    .Where(job => job.ProjectStageOrder == stage.Order)
                    .ToDictionary(job => job.ProjectPackageId!, StringComparer.Ordinal);

                foreach (var target in stage.Targets)
                {
                    if (jobs.ContainsKey(target.PackageId))
                        continue;
                    var job = await builds.TriggerAsync(new ProjectBuildLaunch(
                        delivery.Id,
                        target.PipelineId,
                        target.PackageId,
                        stage.Order,
                        delivery.RepositoryUrl,
                        delivery.Branch,
                        delivery.CommitSha,
                        target.SpecPath,
                        delivery.CommitAuthor,
                        delivery.CommitMessage,
                        target.PromotionGroup), cancellationToken);
                    jobs[target.PackageId] = job;
                }

                if (jobs.Values.Any(job => job.Status is BuildStatus.Failed or BuildStatus.Cancelled))
                {
                    await FailAsync(delivery, "project-build-failed", cancellationToken);
                    return;
                }
                if (jobs.Values.Any(job =>
                        job.Status != BuildStatus.Success && !HasCompleteCandidateAcknowledgement(job)))
                {
                    delivery.Status = ProjectWebhookStatus.Dispatched;
                    delivery.FailureCode = null;
                    delivery.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }
            }

            var candidateJobs = delivery.BuildJobs
                .Where(HasCompleteCandidateAcknowledgement)
                .ToList();
            if (candidateJobs.Count > 0 && candidateJobs.Count != delivery.BuildJobs.Count)
                throw new ValidationException(
                    "Project delivery cannot mix direct publication with candidate promotion.");

            if (candidateJobs.Count > 0)
                await EnsureNativePromotionGatesAsync(delivery, candidateJobs, cancellationToken);

            delivery.Status = candidateJobs.Count > 0
                ? ProjectWebhookStatus.PromotionPending
                : ProjectWebhookStatus.Completed;
            delivery.FailureCode = null;
            delivery.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                candidateJobs.Count > 0
                    ? "Project delivery {DeliveryId} staged all candidates and is awaiting promotion"
                    : "Project delivery {DeliveryId} completed all dependency stages",
                delivery.Id);
        }
        catch (DomainException exception)
        {
            logger.LogWarning(exception, "Project delivery {DeliveryId} failed dispatch validation", delivery.Id);
            await FailAsync(delivery, "dispatch-invalid", cancellationToken);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Project delivery {DeliveryId} contains an invalid persisted plan", delivery.Id);
            await FailAsync(delivery, "dispatch-invalid", cancellationToken);
        }
    }

    private static ProjectDispatchPlan DeserializeAndValidate(ProjectWebhookDelivery delivery)
    {
        if (!delivery.BuildProject.IsActive || string.IsNullOrWhiteSpace(delivery.DispatchPlanJson) ||
            string.IsNullOrWhiteSpace(delivery.ManifestSha256) ||
            string.IsNullOrWhiteSpace(delivery.RepositoryUrl))
        {
            throw new ValidationException("Project delivery is not ready for dispatch.");
        }
        var plan = JsonSerializer.Deserialize<ProjectDispatchPlan>(delivery.DispatchPlanJson, JsonOptions)
            ?? throw new ValidationException("Project dispatch plan is missing.");
        if (plan.Stages is null || plan.Stages.Count is < 1 or > 4096)
            throw new ValidationException("Project dispatch plan has an invalid stage count.");

        var packages = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < plan.Stages.Count; index++)
        {
            var stage = plan.Stages[index];
            if (stage is null || stage.Order != index ||
                stage.Targets is null || stage.Targets.Count is < 1 or > 4096)
                throw new ValidationException("Project dispatch stages are not contiguous and non-empty.");
            foreach (var target in stage.Targets)
            {
                if (target is null)
                    throw new ValidationException("Project dispatch stage contains an invalid target.");
                ProjectBuildTrigger.Validate(new ProjectBuildLaunch(
                    delivery.Id, target.PipelineId, target.PackageId, stage.Order,
                    delivery.RepositoryUrl, delivery.Branch, delivery.CommitSha, target.SpecPath,
                    delivery.CommitAuthor, delivery.CommitMessage, target.PromotionGroup));
                ProjectLookasideSourcePolicy.Validate(target.LookasideSources);
                if (!packages.Add(target.PackageId))
                    throw new ValidationException($"Package '{target.PackageId}' occurs more than once in the dispatch plan.");
            }
        }
        return plan;
    }

    private static void ValidateCurrentBindings(
        ProjectWebhookDelivery delivery,
        ProjectDispatchPlan plan)
    {
        var pipelines = delivery.BuildProject.Pipelines.ToDictionary(pipeline => pipeline.Id);
        foreach (var target in plan.Stages.SelectMany(stage => stage.Targets))
        {
            if (!pipelines.TryGetValue(target.PipelineId, out var pipeline) ||
                pipeline.Status != PipelineStatus.Active ||
                !string.Equals(pipeline.PackageId, target.PackageId, StringComparison.Ordinal) ||
                !string.Equals(pipeline.BuildProfile, target.BuildProfile, StringComparison.Ordinal) ||
                !string.Equals(NormalizePath(pipeline.SpecPath), NormalizePath(target.SpecPath), StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(pipeline.GitUsername) ||
                !string.IsNullOrWhiteSpace(pipeline.GitToken))
            {
                throw new ValidationException(
                    $"Pipeline binding for package '{target.PackageId}' changed after the plan was created.");
            }
        }
    }

    private static void ValidateExistingJobs(
        IReadOnlyCollection<BuildJob> jobs,
        IReadOnlyDictionary<string, (int Order, ProjectDispatchTarget Target)> targets)
    {
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.ProjectPackageId) ||
                !targets.TryGetValue(job.ProjectPackageId, out var planned) ||
                job.ProjectStageOrder != planned.Order || job.PipelineId != planned.Target.PipelineId)
            {
                throw new ValidationException($"Build {job.Id} does not match its project dispatch plan.");
            }
        }
    }

    private static bool HasCompleteCandidateAcknowledgement(BuildJob job)
    {
        if (job.Artifacts.Count == 0)
            return false;
        var first = job.Artifacts[0];
        return first.CandidateRepositoryId is not null &&
               first.PromotionSetId is not null &&
               job.Artifacts.All(artifact =>
                   artifact.CandidateRepositoryId == first.CandidateRepositoryId &&
                   artifact.PromotionSetId == first.PromotionSetId &&
                   artifact.CandidatePackageId is not null &&
                   artifact.CandidateStagedAt is not null);
    }

    private async Task EnsureNativePromotionGatesAsync(
        ProjectWebhookDelivery delivery,
        IReadOnlyCollection<BuildJob> jobs,
        CancellationToken cancellationToken)
    {
        var artifactGroups = jobs
            .SelectMany(job => job.Artifacts.Select(artifact => (Job: job, Artifact: artifact)))
            .GroupBy(item => item.Artifact.PromotionSetId!.Value)
            .ToList();
        var requestedIds = artifactGroups.Select(group => group.Key).ToList();
        var existingIds = await db.NativePromotionGates
            .Where(gate => requestedIds.Contains(gate.Id))
            .Select(gate => gate.Id)
            .ToListAsync(cancellationToken);

        foreach (var group in artifactGroups.Where(group => !existingIds.Contains(group.Key)))
        {
            var repositoryIds = group.Select(item => item.Artifact.CandidateRepositoryId!.Value)
                .Distinct().ToList();
            var promotionGroups = group.Select(item => item.Job.PromotionGroup)
                .Distinct(StringComparer.Ordinal).ToList();
            var targetArchitectures = group.Select(item => item.Job.TargetArchitecture)
                .Distinct(StringComparer.Ordinal).ToList();
            var runnerDigests = group.Select(item => item.Job.RunnerImageDigest)
                .Distinct(StringComparer.Ordinal).ToList();
            if (repositoryIds.Count != 1 || promotionGroups is not [{ Length: > 0 } promotionGroup] ||
                targetArchitectures is not [{ Length: > 0 } architecture] ||
                runnerDigests is not [{ Length: > 0 } runnerDigest])
            {
                throw new ValidationException(
                    $"Promotion set {group.Key} does not have one repository, group, architecture, and runner digest.");
            }

            db.NativePromotionGates.Add(NativePromotionGateManifestPolicy.Create(
                delivery.Id,
                group.Key,
                repositoryIds[0],
                promotionGroup,
                architecture,
                runnerDigest,
                group,
                DateTime.UtcNow));
        }
    }

    private async Task FailAsync(
        ProjectWebhookDelivery delivery,
        string failureCode,
        CancellationToken cancellationToken)
    {
        delivery.Status = ProjectWebhookStatus.Failed;
        delivery.FailureCode = failureCode;
        delivery.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizePath(string? path) =>
        (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
}
