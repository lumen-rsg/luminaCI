using Lumina.SourceService.Data;
using Lumina.SourceService.Services;
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
    Log.Information("Starting Lumina Source Service");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console());

    // Database
    builder.Services.AddDbContext<SourceDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

    // Services
    builder.Services.AddSingleton<SourceUriValidator>();
    builder.Services.AddSingleton<SourceIntegrityService>();
    builder.Services.AddScoped<SourceStorageService>();
    builder.Services.AddScoped<SourceFetchService>();
    builder.Services.AddScoped<SourceFetchQueue>();
    builder.Services.AddScoped<PackageCatalogService>();
    builder.Services.AddHostedService<SourceFetchWorker>();

    // Redis distributed cache
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = builder.Configuration["Redis:ConnectionString"] ?? "redis:6379";
        options.InstanceName = "lumina:source:";
    });
    builder.Services.AddSingleton<RedisCacheService>();

    // MassTransit — SourceService only publishes, no consumers
    builder.Services.AddMassTransit(x =>
    {
        x.ConfigureHealthCheckOptions(options => options.Tags.Add("ready"));

        x.UsingRabbitMq((ctx, cfg) =>
        {
            cfg.Host(builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq", "/", h =>
            {
                h.Username(builder.Configuration["RabbitMQ:Username"] ?? throw new InvalidOperationException("RabbitMQ:Username not configured"));
                h.Password(builder.Configuration["RabbitMQ:Password"] ?? throw new InvalidOperationException("RabbitMQ:Password not configured"));
            });

            cfg.UseMessageRetry(r => r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)));
        });
    });

    builder.Services.AddControllers();
    builder.Services.AddLuminaJwtAuthentication(builder.Configuration);
    builder.Services.AddLuminaAuthorization();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddLuminaCoreReadiness<SourceDbContext>()
        .AddConfiguredHttpReadiness("minio", "MinIO:Endpoint", "minio:9000", "/minio/health/ready")
        .AddWritableDirectoriesReadiness(
            builder.Configuration["Source:TempDir"] ?? "/tmp/source-fetch",
            builder.Configuration["Source:SourcesDir"] ?? "/opt/lumina/sources");

    var app = builder.Build();

    // Apply EF Core migrations (fail-closed). Errors propagate to the top-level
    // handler rather than being swallowed as "tables may already exist".
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<SourceDbContext>();
        await DatabaseInitializer.MigrateAsync(db);
        Log.Information("Source database schema applied (EF Core migrations)");
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
    Log.Fatal(ex, "Source Service terminated unexpectedly");
    // Propagate so the host exits with a non-zero code. Swallowing here would
    // make a crash look like a clean exit (code 0), so Docker's restart policy
    // could not tell them apart (SEC-024). The finally below still flushes logs.
    throw;
}
finally
{
    Log.CloseAndFlush();
}
