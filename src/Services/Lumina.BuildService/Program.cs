using Lumina.BuildService.Consumers;
using Lumina.BuildService.Data;
using Lumina.BuildService.Services;
using Lumina.Shared.Events;
using Lumina.Shared.Extensions;
using Lumina.Shared.Security;
using Lumina.Web.Shared;
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

    // At-rest secret encryption (AES-256-GCM). The MasterKey comes from
    // Secrets:MasterKey (SECRETS_MASTER_KEY env var) and is required — the
    // service refuses to start without it so secrets are never stored in
    // plaintext by accident. See AesSecretProtector for the on-disk format.
    builder.Services.AddSingleton<ISecretProtector, AesSecretProtector>();

    // Services. DockerBuildService is registered both concretely (BuildsController
    // depends on its LogSubscription / log-streaming surface) and as IBuildLauncher
    // (PipelineEngine depends on the abstraction so it can be unit-tested without
    // a Docker daemon). The same instance satisfies both — AddScoped<X>() then
    // AddScoped<IX>(sp => sp.GetRequiredService<X>()) keeps it a single scoped object.
    builder.Services.AddScoped<DockerBuildService>();
    builder.Services.AddScoped<IBuildLauncher>(sp => sp.GetRequiredService<DockerBuildService>());
    builder.Services.AddScoped<ISigningKeyGate, SigningKeyGate>();
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
        x.AddConsumer<GetArtifactSignatureConsumer>();

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
                e.ConfigureConsumer<GetArtifactSignatureConsumer>(ctx);
            });

            cfg.UseMessageRetry(r => r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)));
        });
    });

    builder.Services.AddControllers();
    builder.Services.AddLuminaJwtAuthentication(builder.Configuration);
    builder.Services.AddLuminaAuthorization();
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

    // Apply EF Core migrations (fail-closed). Replaces the legacy
    // GenerateCreateScript() + hand-written ALTER TABLE approach, which wrapped
    // every DDL statement in a catch-all that hid real failures (connection
    // refused, permission denied) as "tables may already exist". See
    // DatabaseInitializer for the legacy cut-over stamp and why errors now
    // propagate instead of being swallowed.
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();
        await DatabaseInitializer.MigrateAsync(db);
        Log.Information("Build database schema applied (EF Core migrations)");
    }

    // Ensure required host directories exist for build artifacts and sources
    foreach (var dir in new[] { "/app/builds", "/opt/lumina/builds", "/opt/lumina/sources", "/opt/lumina/extra-sources/pipelines" })
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

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHealthChecks("/health");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Build Service terminated unexpectedly");
    // Propagate so the host exits with a non-zero code. Swallowing here would
    // make a crash look like a clean exit (code 0), so Docker's restart policy
    // could not tell them apart (SEC-024). The finally below still flushes logs.
    throw;
}
finally
{
    Log.CloseAndFlush();
}
