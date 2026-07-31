using System.Text.Json;
using Lumina.BuildService.Data;
using Lumina.Shared.Events;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

public sealed record GitHubPush(
    string CommitSha,
    string Branch,
    IReadOnlySet<string> ChangedPaths,
    string? Author,
    string? Message);

public interface IRepositorySnapshotPublisher
{
    Task PublishAsync(RepositorySnapshotRequested request, CancellationToken cancellationToken);
}

public sealed class RepositorySnapshotPublisher : IRepositorySnapshotPublisher
{
    private readonly MassTransit.IPublishEndpoint _publishEndpoint;

    public RepositorySnapshotPublisher(MassTransit.IPublishEndpoint publishEndpoint)
    {
        _publishEndpoint = publishEndpoint;
    }

    public Task PublishAsync(RepositorySnapshotRequested request, CancellationToken cancellationToken) =>
        _publishEndpoint.Publish(request, cancellationToken);
}

public sealed class ProjectWebhookService
{
    private readonly BuildDbContext _db;
    private readonly IRepositorySnapshotPublisher _publish;

    public ProjectWebhookService(BuildDbContext db, IRepositorySnapshotPublisher publish)
    {
        _db = db;
        _publish = publish;
    }

    public Task<BuildProject?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        _db.BuildProjects.SingleOrDefaultAsync(project => project.Id == projectId, cancellationToken);

    public async Task<ProjectWebhookDelivery> QueueSnapshotAsync(
        BuildProject project,
        string providerDeliveryId,
        GitHubPush push,
        CancellationToken cancellationToken)
    {
        if (!project.IsActive)
            throw new ValidationException("Build project is inactive.");
        if (!string.Equals(project.GitBranch, push.Branch, StringComparison.Ordinal))
            throw new ValidationException("Webhook branch does not match the build project branch.");
        if (!string.IsNullOrWhiteSpace(project.GitUsername) ||
            !string.IsNullOrWhiteSpace(project.GitToken))
        {
            throw new ValidationException(
                "Credentialed repository snapshots require a source credential broker and are not enabled yet.");
        }
        if (providerDeliveryId.Length is < 1 or > 128 || providerDeliveryId.Any(char.IsControl))
            throw new ValidationException("GitHub delivery ID is invalid.");

        var existing = await _db.ProjectWebhookDeliveries.SingleOrDefaultAsync(
            delivery => delivery.BuildProjectId == project.Id &&
                        delivery.ProviderDeliveryId == providerDeliveryId,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.Status == ProjectWebhookStatus.SnapshotPending)
            {
                await _publish.PublishAsync(CreateSnapshotRequest(existing, project), cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
            }
            return existing;
        }

        var now = DateTime.UtcNow;
        var delivery = new ProjectWebhookDelivery
        {
            Id = Guid.NewGuid(),
            BuildProjectId = project.Id,
            ProviderDeliveryId = providerDeliveryId,
            RepositoryUrl = project.GitRepoUrl,
            CommitSha = push.CommitSha,
            Branch = push.Branch,
            ChangedPaths = push.ChangedPaths.Order(StringComparer.Ordinal).ToList(),
            CommitAuthor = Truncate(push.Author, 256),
            CommitMessage = Truncate(push.Message, 2048),
            Status = ProjectWebhookStatus.SnapshotPending,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.ProjectWebhookDeliveries.Add(delivery);
        await _publish.PublishAsync(CreateSnapshotRequest(delivery, project), cancellationToken);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return delivery;
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            var winner = await _db.ProjectWebhookDeliveries.SingleOrDefaultAsync(
                item => item.BuildProjectId == project.Id &&
                        item.ProviderDeliveryId == providerDeliveryId,
                cancellationToken);
            if (winner is not null)
                return winner;
            throw;
        }
    }

    private static RepositorySnapshotRequested CreateSnapshotRequest(
        ProjectWebhookDelivery delivery,
        BuildProject project) => new(
        delivery.Id,
        project.Id,
        project.GitRepoUrl,
        delivery.CommitSha,
        project.ManifestPath,
        delivery.CreatedAt);

    public static GitHubPush ParseGitHubPush(JsonElement payload)
    {
        var commit = payload.TryGetProperty("after", out var after) ? after.GetString() : null;
        var reference = payload.TryGetProperty("ref", out var refElement) ? refElement.GetString() : null;
        if (commit is not { Length: 40 or 64 } || commit.Any(character => !Uri.IsHexDigit(character)))
            throw new ValidationException("GitHub push does not contain a full commit SHA.");
        const string BranchPrefix = "refs/heads/";
        if (reference is null || !reference.StartsWith(BranchPrefix, StringComparison.Ordinal))
            throw new ValidationException("GitHub push is not for a branch.");

        string? author = null;
        string? message = null;
        if (payload.TryGetProperty("head_commit", out var head) && head.ValueKind == JsonValueKind.Object)
        {
            message = head.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()?.Split('\n')[0]
                : null;
            if (head.TryGetProperty("author", out var authorElement))
            {
                author = authorElement.TryGetProperty("username", out var username)
                    ? username.GetString()
                    : authorElement.TryGetProperty("name", out var name) ? name.GetString() : null;
            }
        }

        return new GitHubPush(
            commit.ToLowerInvariant(),
            reference[BranchPrefix.Length..],
            WebhookPathFilter.ExtractChangedPaths(payload),
            author,
            message);
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
}
