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

    builder.Services.AddHttpClient(); // IHttpClientFactory for inter-service calls
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();

    // Allow unlimited file uploads for extra sources
    builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
    {
        options.MultipartBodyLengthLimit = long.MaxValue;
        options.ValueLengthLimit = int.MaxValue;
    });
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxRequestBodySize = long.MaxValue;
        options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(30);
        options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(30);
    });
    builder.Services.AddSwaggerGen();
    builder.Services.AddHealthChecks();

    var app = builder.Build();

    // Create tables if not exist
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();

        // Ensure PostgreSQL extensions required by the schema (e.g., hstore for PipelineStep.Configuration)
        try
        {
            await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS hstore");
            Log.Information("Ensured hstore extension is available");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not create hstore extension — some features may not work");
        }

        await CreateTablesWithScriptAsync(db);
    }

    // Ensure required host directories exist for build artifacts and sources
    foreach (var dir in new[] { "/app/builds", "/opt/lumina/builds", "/opt/lumina/sources", "/opt/lumina/extra-sources/pipelines", "/opt/lumina/extra-sources/builds" })
    {
        try
        {
            Directory.CreateDirectory(dir);
            Log.Information("Ensured directory exists: {Dir}", dir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not create directory {Dir} (may already exist or be a volume mount)", dir);
        }
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
        app.UseDeveloperExceptionPage();
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
        ("\"build\".\"pipeline_steps\"", "\"Configuration\"", "hstore DEFAULT ''"),
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
