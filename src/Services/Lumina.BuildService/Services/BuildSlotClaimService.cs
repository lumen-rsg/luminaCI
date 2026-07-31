using Lumina.BuildService.Data;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

public interface IBuildSlotClaimer
{
    Task ClaimAsync(BuildJob job, CancellationToken cancellationToken = default);
}

/// <summary>
/// Claims one distributed build slot under a PostgreSQL advisory transaction
/// lock. The process-local coordinator prevents excess work in one replica;
/// this service makes the same limit authoritative across all replicas.
/// </summary>
public sealed class BuildSlotClaimService(
    BuildDbContext db,
    BuildExecutionCoordinator execution) : IBuildSlotClaimer
{
    public async Task ClaimAsync(
        BuildJob job,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(4867564)",
                cancellationToken);

            var now = DateTime.UtcNow;
            var active = await db.BuildJobs.CountAsync(item =>
                item.Status == BuildStatus.Building
                && item.LeaseExpiresAt != null
                && item.LeaseExpiresAt > now,
                cancellationToken);
            if (active < execution.MaxConcurrentBuilds)
            {
                job.Status = BuildStatus.Building;
                job.StartedAt = now;
                job.LeaseOwner = execution.WorkerId;
                job.LastHeartbeatAt = now;
                job.LeaseExpiresAt = now + execution.LeaseDuration;
                job.DeadlineAt = now + execution.MaxBuildDuration;
                db.BuildJobs.Update(job);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            await transaction.RollbackAsync(cancellationToken);
            await Task.Delay(execution.HeartbeatInterval, cancellationToken);
        }
    }
}
