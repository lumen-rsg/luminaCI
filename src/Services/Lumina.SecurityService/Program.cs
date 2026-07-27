using Lumina.SecurityService.Consumers;
using Lumina.SecurityService.Data;
using Lumina.SecurityService.Health;
using Lumina.SecurityService.Services;
using Lumina.Shared.Extensions;
using Lumina.Web.Shared;
using Lumina.Web.Shared.Health;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Lumina Security Service");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console());

    builder.Services.AddDbContext<SecurityDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

    builder.Services.AddScoped<PgpSigningService>();
    builder.Services.AddScoped<HashService>();

    // Redis distributed cache
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = builder.Configuration["Redis:ConnectionString"] ?? "redis:6379";
        options.InstanceName = "lumina:security:";
    });
    builder.Services.AddSingleton<RedisCacheService>();

    // MassTransit with RabbitMQ
    builder.Services.AddMassTransit(x =>
    {
        x.ConfigureHealthCheckOptions(options => options.Tags.Add("ready"));

        x.AddConsumer<HashStoreRequestedConsumer>();
        x.AddConsumer<PackageSigningRequestedConsumer>();
        x.AddConsumer<GetActiveSigningKeyConsumer>();
        x.AddConsumer<GetActivePublicKeyConsumer>();
        x.AddConsumer<GetPublicKeyConsumer>();
        x.AddEntityFrameworkOutbox<SecurityDbContext>(outbox =>
        {
            outbox.UsePostgres();
            outbox.UseBusOutbox();
            outbox.DuplicateDetectionWindow = TimeSpan.FromDays(7);
        });

        x.UsingRabbitMq((ctx, cfg) =>
        {
            cfg.Host(builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq", "/", h =>
            {
                h.Username(builder.Configuration["RabbitMQ:Username"] ?? throw new InvalidOperationException("RabbitMQ:Username not configured"));
                h.Password(builder.Configuration["RabbitMQ:Password"] ?? throw new InvalidOperationException("RabbitMQ:Password not configured"));
            });

            cfg.ReceiveEndpoint("lumina-security-service", e =>
            {
                e.UseEntityFrameworkOutbox<SecurityDbContext>(ctx);
                e.ConfigureConsumer<HashStoreRequestedConsumer>(ctx);
                e.ConfigureConsumer<PackageSigningRequestedConsumer>(ctx);
                e.ConfigureConsumer<GetActiveSigningKeyConsumer>(ctx);
                e.ConfigureConsumer<GetActivePublicKeyConsumer>(ctx);
                e.ConfigureConsumer<GetPublicKeyConsumer>(ctx);
            });

            cfg.UseMessageRetry(r => r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)));
        });
    });

    builder.Services.AddControllers();
    builder.Services.AddLuminaJwtAuthentication(builder.Configuration);
    builder.Services.AddLuminaAuthorization();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddLuminaCoreReadiness<SecurityDbContext>()
        .AddCheck<SigningReadinessHealthCheck>("signing-key", tags: ["ready"])
        .AddWritableDirectoriesReadiness(
            builder.Configuration["Gpg:KeyDirectory"] ?? "/app/keys",
            Environment.GetEnvironmentVariable("GNUPGHOME") ?? "/app/.gnupg");

    var app = builder.Build();

    // Apply EF Core migrations (fail-closed), then auto-generate a default PGP
    // key if none exists. Migration errors propagate to the top-level handler
    // rather than being swallowed as "tables may already exist".
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<SecurityDbContext>();
        await DatabaseInitializer.MigrateAsync(db);
        Log.Information("Security database schema applied (EF Core migrations)");

        // Auto-generate a default PGP key if none exists
        var pgpService = scope.ServiceProvider.GetRequiredService<PgpSigningService>();
        await pgpService.ReconcileLegacyKeyFingerprintsAsync();
        var keys = await pgpService.ListKeysAsync();
        if (keys.All(k => !k.IsActive))
        {
            await pgpService.GenerateKeyAsync("Lumina CI", "lumina@ci.local", "system");
            Log.Information("Auto-generated default PGP key (Lumina CI / lumina@ci.local)");
        }
        else
        {
            Log.Information("Found {Count} existing PGP key(s)", keys.Count);
        }
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    app.MapLuminaHealthChecks();
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Security Service terminated unexpectedly");
    // Propagate so the host exits with a non-zero code. Swallowing here would
    // make a crash look like a clean exit (code 0), so Docker's restart policy
    // could not tell them apart (SEC-024). The finally below still flushes logs.
    throw;
}
finally
{
    Log.CloseAndFlush();
}
