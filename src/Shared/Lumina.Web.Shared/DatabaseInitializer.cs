using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Lumina.Web.Shared;

/// <summary>
/// Fail-closed database schema initializer.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the legacy startup scheme of <c>db.Database.GenerateCreateScript()</c>
/// followed by hand-written <c>ALTER TABLE ... ADD COLUMN</c> blocks, all wrapped in
/// catch-all <c>catch (Exception)</c> that swallowed real failures (connection
/// refused, permission denied, malformed SQL) as "tables may already exist". That
/// made a service start up "healthy" with a missing or broken schema — silently
/// losing data or crashing on the first request.
/// </para>
/// <para>
/// This runner is <b>fail-closed</b>: any genuine migration error propagates out,
/// so the caller's top-level handler logs fatal and the process exits non-zero,
/// surfacing the problem instead of masking it.
/// </para>
/// <para>
/// <b>Legacy bootstrap.</b> Existing deployments were created by the old
/// <c>GenerateCreateScript()</c> path and therefore have the tables already but no
/// <c>__EFMigrationsHistory</c> (EF migrations were never run against them). If we
/// called <c>MigrateAsync()</c> blindly, EF would replay the baseline migration's
/// <c>CREATE TABLE</c> and fail on "table already exists". So the first time we see
/// a context with an empty migration history but an already-present schema, we
/// <b>stamp</b> the baseline migration(s) as applied (insert history rows without
/// running their DDL) and then let <c>MigrateAsync</c> pick up from there. This is
/// a deliberate, narrowly-scoped cutover step — it is <i>not</i> error-swallowing:
/// a probe that fails for any reason other than "table absent" is propagated.
/// </para>
/// </remarks>
public static class DatabaseInitializer
{
    public static async Task MigrateAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        var dbFacade = db.Database;
        var migrator = dbFacade.GetService<IMigrator>()
            ?? throw new InvalidOperationException(
                $"No EF Core migration service is registered for {db.GetType().Name}. " +
                "Ensure the context is configured with a relational provider (e.g. UseNpgsql).");

        // Pending migrations for this context (from its own Migrations assembly).
        // Null when the context has no migrations at all.
        var migrationsAssembly = db.GetService<IMigrationsAssembly>();
        var hasMigrations = migrationsAssembly != null && migrationsAssembly.Migrations.Count > 0;

        if (!hasMigrations)
        {
            // A context with no migrations has nothing to apply; nothing to do.
            return;
        }

        var appliedIds = await GetAppliedMigrationIdsAsync(db, dbFacade, cancellationToken);

        if (appliedIds.Count == 0 && await LegacySchemaExistsAsync(db, cancellationToken))
        {
            // Cut-over path: the DB was created by the old GenerateCreateScript()
            // approach (tables present, migration history absent). Stamp every
            // pending baseline migration as already-applied instead of replaying
            // CREATE TABLE statements that would collide with the existing schema.
            var pending = migrationsAssembly!.Migrations
                .Select(kv => kv.Key)
                .Where(id => !appliedIds.Contains(id))
                .ToList();

            if (pending.Count > 0)
            {
                await StampMigrationsAsync(db, dbFacade, pending, cancellationToken);
            }
        }

        // Apply any remaining pending migrations. No catch-all: a real failure
        // (connection lost, permission denied, model/DB drift, bad SQL) propagates
        // to the caller's top-level handler → Log.Fatal → process exits non-zero,
        // so the service reports unhealthy rather than serving with a broken schema.
        await migrator.MigrateAsync();
    }

    /// <summary>
    /// Reads the set of migration ids recorded in <c>__EFMigrationsHistory</c>.
    /// Returns an empty set when the history table does not exist yet (it is
    /// created on the first real migration application).
    /// </summary>
    private static async Task<HashSet<string>> GetAppliedMigrationIdsAsync(
        DbContext db,
        DatabaseFacade facade,
        CancellationToken cancellationToken)
    {
        if (!await facade.CanConnectAsync(cancellationToken))
        {
            // DB not reachable / not created — no migrations applied. The caller's
            // subsequent MigrateAsync (or this method's propagation) surfaces the
            // connection problem.
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var historyRepository = db.GetService<IHistoryRepository>();
        if (historyRepository is null || !await historyRepository.ExistsAsync(cancellationToken))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var rows = await db.Database.SqlQueryRaw<string>(
            "SELECT \"MigrationId\" AS \"Value\" FROM \"__EFMigrationsHistory\"")
            .ToListAsync(cancellationToken);

        return new HashSet<string>(rows, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether the schema for this context already exists in the DB —
    /// i.e. it was created by the legacy <c>GenerateCreateScript()</c> startup
    /// path rather than by EF migrations. Probes the first entity's table via
    /// <c>information_schema.tables</c>. Any error here propagates (fail-closed):
    /// we never silently treat an unreadable catalog as "legacy schema present".
    /// </summary>
    private static async Task<bool> LegacySchemaExistsAsync(DbContext db, CancellationToken cancellationToken)
    {
        var firstEntity = db.Model.GetEntityTypes().FirstOrDefault();
        if (firstEntity is null)
        {
            return false;
        }

        var schema = firstEntity.GetSchema() ?? "public";
        var table = firstEntity.GetTableName();
        if (string.IsNullOrEmpty(table))
        {
            return false;
        }

        // Parameterized probe of the information schema.
        var exists = await db.Database.SqlQueryRaw<bool>(
            @"SELECT EXISTS (
                SELECT 1 FROM information_schema.tables
                WHERE table_schema = {0} AND table_name = {1}
              ) AS ""Value""",
            new object[] { schema, table })
            .FirstOrDefaultAsync(cancellationToken);

        return exists;
    }

    /// <summary>
    /// Records the given migration ids as applied in <c>__EFMigrationsHistory</c>
    /// without executing their DDL — used once, to cut over a pre-existing schema
    /// that was created by the legacy script. Creates the history table if needed.
    /// </summary>
    private static async Task StampMigrationsAsync(
        DbContext db,
        DatabaseFacade facade,
        IReadOnlyList<string> migrationIds,
        CancellationToken cancellationToken)
    {
        var historyRepository = db.GetService<IHistoryRepository>()
            ?? throw new InvalidOperationException("EF Core history repository is not available.");

        if (!await historyRepository.ExistsAsync(cancellationToken))
        {
            var createScript = historyRepository.GetCreateScript();
            await facade.ExecuteSqlRawAsync(createScript, cancellationToken);
        }

        // ProductVersion is informational only (displayed by EF tooling); EF
        // accepts any non-null string. Stamp with the EF Core runtime version so
        // the history rows match what EF would write on a normal migration.
        var productVersion = typeof(Migration).Assembly
            .GetName()
            .Version?.ToString() ?? "10.0.0";

        foreach (var migrationId in migrationIds)
        {
            await facade.ExecuteSqlRawAsync(
                @"INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                  VALUES ({0}, {1})",
                new object[] { migrationId, productVersion },
                cancellationToken);
        }
    }
}
