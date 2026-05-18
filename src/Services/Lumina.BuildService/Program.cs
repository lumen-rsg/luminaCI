using Lumina.BuildService.Consumers;
using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.Events;
using Lumina.Shared.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Lumina Build Service");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console());

    // Database
    builder.Services.AddDbContext<BuildDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

    // Services
    builder.Services.AddScoped<DockerBuildService>();
    builder.Services.AddScoped<PipelineEngine>();

    // Redis distributed cache
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = builder.Configuration["Redis:ConnectionString"] ?? "redis:6379";
        options.InstanceName = "lumina:build:";
    });
    builder.Services.AddSingleton<RedisCacheService>();

    // MassTransit with RabbitMQ
    builder.Services.AddMassTransit(x =>
    {
        x.AddConsumer<CveScanCompletedConsumer>();
        x.AddConsumer<PackageSignedConsumer>();
        x.AddConsumer<BuildTriggerFromConfigConsumer>();

        x.UsingRabbitMq((ctx, cfg) =>
        {
            cfg.Host(builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq", "/", h =>
            {
                h.Username(builder.Configuration["RabbitMQ:Username"] ?? throw new InvalidOperationException("RabbitMQ:Username not configured"));
                h.Password(builder.Configuration["RabbitMQ:Password"] ?? throw new InvalidOperationException("RabbitMQ:Password not configured"));
            });

            cfg.ReceiveEndpoint("lumina-build-service", e =>
            {
                e.ConfigureConsumer<CveScanCompletedConsumer>(ctx);
                e.ConfigureConsumer<PackageSignedConsumer>(ctx);
                e.ConfigureConsumer<BuildTriggerFromConfigConsumer>(ctx);
            });

            cfg.UseMessageRetry(r => r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)));
        });
    });

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddHealthChecks();

    var app = builder.Build();

    // Create tables if not exist
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();
        await CreateTablesWithScriptAsync(db);
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
    Log.Fatal(ex, "Build Service terminated unexpectedly");
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

    // Add columns that may be missing from earlier schema versions
    var migrations = new (string Table, string Column, string Def)[]
    {
        ("\"build\".\"pipelines\"", "\"SpecContent\"", "text NULL"),
        ("\"build\".\"build_jobs\"", "\"SpecContent\"", "text NOT NULL DEFAULT ''"),
        ("\"build\".\"build_jobs\"", "\"SourceUrl\"", "text NULL"),
        ("\"build\".\"build_jobs\"", "\"CommitSha\"", "text NULL"),
        ("\"build\".\"build_jobs\"", "\"Branch\"", "text NULL"),
        ("\"build\".\"build_jobs\"", "\"CommitMessage\"", "text NULL"),
        ("\"build\".\"build_jobs\"", "\"CommitAuthor\"", "text NULL"),
    };

    foreach (var (table, column, def) in migrations)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE {table} ADD COLUMN {column} {def}");
            Log.Information("Added column {Table}.{Column}", table, column);
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "Column {Table}.{Column} already exists, skipped", table, column);
        }
    }
}
