using Lumina.ScannerService.Consumers;
using Lumina.ScannerService.Data;
using Lumina.ScannerService.Services;
using Lumina.Shared.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Lumina Scanner Service");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console());

    builder.Services.AddDbContext<ScannerDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

    // HTTP client for Trivy Server API
    builder.Services.AddHttpClient("TrivyServer");

    // Redis distributed cache
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = builder.Configuration["Redis:ConnectionString"] ?? "redis:6379";
        options.InstanceName = "lumina:scanner:";
    });
    builder.Services.AddSingleton<RedisCacheService>();

    builder.Services.AddScoped<TrivyScannerService>();

    // MassTransit with RabbitMQ
    builder.Services.AddMassTransit(x =>
    {
        x.AddConsumer<CveScanRequestedConsumer>();

        x.UsingRabbitMq((ctx, cfg) =>
        {
            cfg.Host(builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq", "/", h =>
            {
                h.Username(builder.Configuration["RabbitMQ:Username"] ?? throw new InvalidOperationException("RabbitMQ:Username not configured"));
                h.Password(builder.Configuration["RabbitMQ:Password"] ?? throw new InvalidOperationException("RabbitMQ:Password not configured"));
            });

            cfg.ReceiveEndpoint("lumina-scanner-service", e =>
            {
                e.ConfigureConsumer<CveScanRequestedConsumer>(ctx);
            });

            cfg.UseMessageRetry(r => r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)));
        });
    });

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddHealthChecks();

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ScannerDbContext>();
        await CreateTablesWithScriptAsync(db);
        await RunMigrationsAsync(db);
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.MapControllers();
    app.MapHealthChecks("/health");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Scanner Service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

static async Task CreateTablesWithScriptAsync(DbContext db)
{
    var script = db.Database.GenerateCreateScript();
    try
    {
        await db.Database.ExecuteSqlRawAsync(script);
        Log.Information("Database tables created/verified successfully");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Table creation skipped (tables may already exist)");
    }
}

static async Task RunMigrationsAsync(DbContext db)
{
    var migrations = new (string Sql, string Description)[]
    {
        ("ALTER TABLE \"CveReports\" ALTER COLUMN \"ScannerType\" TYPE varchar(50)", "Widen ScannerType varchar(20)->varchar(50)"),
    };

    foreach (var (sql, desc) in migrations)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql);
            Log.Information("Applied migration: {Description}", desc);
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "Migration skipped (may already be applied): {Description}", desc);
        }
    }
}
