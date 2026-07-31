using Lumina.RepositoryService.Data;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.RepositoryService.Services;

internal sealed class RepositoryPromotionHostedService(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<RepositoryPromotionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("RepositoryPromotion:Enabled", false))
        {
            logger.LogInformation("Atomic repository promotion reconciler is disabled");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Atomic repository promotion reconciliation failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await using var queryScope = scopes.CreateAsyncScope();
        var db = queryScope.ServiceProvider.GetRequiredService<RepositoryDbContext>();
        var ids = await db.PromotionSets.AsNoTracking()
            .Where(item => item.Status == PromotionSetStatus.Passed)
            .OrderBy(item => item.GateCompletedAt)
            .Select(item => item.Id)
            .Take(16)
            .ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RepositoryPromotionService>()
                    .PromoteAsync(id, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Promotion set {PromotionSetId} remains pending", id);
            }
        }
    }
}
