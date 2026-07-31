using Lumina.BuildService.Data;
using Lumina.Shared.DTOs;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;

namespace Lumina.BuildService.Services.PackageGraph;

public sealed record ProjectBuildLaunch(
    Guid DeliveryId,
    Guid PipelineId,
    string PackageId,
    int StageOrder,
    string RepositoryUrl,
    string Branch,
    string CommitSha,
    string SpecPath,
    string? CommitAuthor,
    string? CommitMessage);

public interface IProjectBuildTrigger
{
    Task<BuildJob> TriggerAsync(
        ProjectBuildLaunch launch,
        CancellationToken cancellationToken);
}

/// <summary>
/// Uses a fresh service scope so PipelineEngine's idempotency conflict recovery
/// cannot clear the dispatcher's EF change tracker. The deterministic key also
/// reconnects a job that was created immediately before a process crash.
/// </summary>
public sealed class ProjectBuildTrigger(IServiceScopeFactory scopeFactory) : IProjectBuildTrigger
{
    public async Task<BuildJob> TriggerAsync(
        ProjectBuildLaunch launch,
        CancellationToken cancellationToken)
    {
        var request = CreateRequest(launch);
        await using var scope = scopeFactory.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<PipelineEngine>();
        var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();
        var job = await engine.TriggerBuildAsync(
            launch.PipelineId,
            request);

        if (job.ProjectWebhookDeliveryId.HasValue &&
            (job.ProjectWebhookDeliveryId != launch.DeliveryId ||
             !string.Equals(job.ProjectPackageId, launch.PackageId, StringComparison.Ordinal) ||
             job.ProjectStageOrder != launch.StageOrder))
        {
            throw new ConflictException(
                $"Build {job.Id} is already linked to a different project dispatch target.");
        }

        job.ProjectWebhookDeliveryId = launch.DeliveryId;
        job.ProjectPackageId = launch.PackageId;
        job.ProjectStageOrder = launch.StageOrder;
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    public static TriggerBuildRequest CreateRequest(ProjectBuildLaunch launch)
    {
        Validate(launch);
        var sourceUrl = $"git://{launch.RepositoryUrl}#branch={launch.Branch}" +
                        $"&specPath={launch.SpecPath}&commit={launch.CommitSha}";
        return new TriggerBuildRequest(
            Path.GetFileName(launch.SpecPath),
            string.Empty,
            sourceUrl,
            $"project:{launch.DeliveryId:N}",
            launch.CommitSha,
            launch.Branch,
            launch.CommitMessage,
            launch.CommitAuthor,
            $"project:{launch.DeliveryId:N}:{launch.PackageId}");
    }

    public static void Validate(ProjectBuildLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        _ = BuildProjectPolicy.NormalizePackageId(launch.PackageId);
        if (launch.DeliveryId == Guid.Empty || launch.PipelineId == Guid.Empty || launch.StageOrder < 0)
            throw new ValidationException("Project build identity is invalid.");
        if (!Uri.TryCreate(launch.RepositoryUrl, UriKind.Absolute, out var repository) ||
            repository.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(repository.Host) ||
            !string.IsNullOrEmpty(repository.UserInfo) || !string.IsNullOrEmpty(repository.Query) ||
            !string.IsNullOrEmpty(repository.Fragment))
        {
            throw new ValidationException("Project build repository URL is invalid.");
        }
        if (launch.CommitSha is not { Length: 40 or 64 } ||
            launch.CommitSha.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ValidationException("Project build commit SHA is invalid.");
        }
        var branch = launch.Branch ?? string.Empty;
        if (branch.Length is < 1 or > 256 ||
            branch.Any(character => char.IsControl(character) || character is '&' or '=' or '#'))
        {
            throw new ValidationException("Project build branch cannot be represented safely.");
        }
        var specPath = (launch.SpecPath ?? string.Empty).Replace('\\', '/');
        if (specPath.Length is < 6 or > 4096 || !specPath.EndsWith(".spec", StringComparison.Ordinal) ||
            specPath.StartsWith('/') || specPath.Split('/').Any(segment => segment is "" or "." or "..") ||
            specPath.Any(character => char.IsControl(character) || character is '&' or '=' or '#'))
        {
            throw new ValidationException("Project build spec path cannot be represented safely.");
        }
    }
}
