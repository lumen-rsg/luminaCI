using Lumina.BuildService.Consumers;
using Lumina.BuildService.Data;
using Lumina.BuildService.Health;
using Lumina.BuildService.Services;
using Lumina.BuildService.Services.PackageGraph;
using Lumina.Shared.Events;
using Lumina.Shared.Extensions;
using Lumina.Shared.Security;
using Lumina.Web.Shared;
using Lumina.Web.Shared.Health;
using Lumina.Web.Shared.Observability;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Minio;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Lumina Build Service");

    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddLuminaOpenTelemetry(
        builder.Configuration, "lumina-build-service");

    builder.Host.UseSerilog((ctx, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    // Database
    builder.Services.AddDbContext<BuildDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

    // At-rest secret encryption (AES-256-GCM). The MasterKey comes from
    // Secrets:MasterKey (SECRETS_MASTER_KEY env var) and is required — the
    // service refuses to start without it so secrets are never stored in
    // plaintext by accident. See AesSecretProtector for the on-disk format.
    builder.Services.AddSingleton<ISecretProtector, AesSecretProtector>();
    builder.Services.AddSingleton<BuildExecutionCoordinator>();
    builder.Services.AddHostedService<BuildMonitorHostedService>();

    // Services. DockerBuildService is registered both concretely (BuildsController
    // depends on its LogSubscription / log-streaming surface) and as IBuildLauncher
    // (PipelineEngine depends on the abstraction so it can be unit-tested without
    // a Docker daemon). The same instance satisfies both — AddScoped<X>() then
    // AddScoped<IX>(sp => sp.GetRequiredService<X>()) keeps it a single scoped object.
    builder.Services.AddScoped<DockerBuildService>();
    builder.Services.AddScoped<IBuildLauncher>(sp => sp.GetRequiredService<DockerBuildService>());
    builder.Services.AddSingleton<IRpmArtifactValidator, RpmArtifactValidator>();
    builder.Services.AddScoped<ISigningKeyGate, SigningKeyGate>();
    builder.Services.AddScoped<PipelineEngine>();
    builder.Services.AddScoped<BuildProjectService>();
    builder.Services.AddScoped<ProjectWebhookService>();
    builder.Services.AddScoped<IRepositorySnapshotPublisher, RepositorySnapshotPublisher>();
    builder.Services.AddScoped<ProjectSnapshotPlanService>();
    builder.Services.AddScoped<IRepositorySnapshotStreamProvider, RepositorySnapshotStreamProvider>();
    builder.Services.AddScoped<ProjectDispatchService>();
    builder.Services.AddScoped<IProjectBuildTrigger, ProjectBuildTrigger>();
    builder.Services.AddHostedService<ProjectDispatchHostedService>();
    builder.Services.AddScoped<PipelineRunCoordinator>();
    builder.Services.AddScoped<ArtifactStorageService>();
    builder.Services.AddHttpClient("ArtifactStorage", client =>
        client.Timeout = TimeSpan.FromMinutes(2));
    builder.Services.AddHttpClient("RepositorySnapshots", client =>
        client.Timeout = TimeSpan.FromMinutes(30));
    builder.Services.AddMinio(client => client
        .WithEndpoint(builder.Configuration["MinIO:Endpoint"] ?? "minio:9000")
        .WithCredentials(
            builder.Configuration["MinIO:AccessKey"] ?? throw new InvalidOperationException("MinIO:AccessKey not configured"),
            builder.Configuration["MinIO:SecretKey"] ?? throw new InvalidOperationException("MinIO:SecretKey not configured"))
        .WithSSL(false)
        .Build());

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
        x.ConfigureHealthCheckOptions(options => options.Tags.Add("ready"));

        x.AddConsumer<CveScanCompletedConsumer>();
        x.AddConsumer<PackageSignedConsumer>();
        x.AddConsumer<PackageSigningFaultConsumer>();
        x.AddConsumer<PackagePublishedConsumer>();
        x.AddConsumer<PackagePublishFaultConsumer>();
        x.AddConsumer<GetArtifactSignatureConsumer>();
        x.AddConsumer<GetArtifactLocationConsumer>();
        x.AddConsumer<RepositorySnapshotCompletedConsumer>();
        x.AddEntityFrameworkOutbox<BuildDbContext>(outbox =>
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

            cfg.ReceiveEndpoint("lumina-build-service", e =>
            {
                e.UseEntityFrameworkOutbox<BuildDbContext>(ctx);
                e.ConfigureConsumer<CveScanCompletedConsumer>(ctx);
                e.ConfigureConsumer<PackageSignedConsumer>(ctx);
                e.ConfigureConsumer<PackageSigningFaultConsumer>(ctx);
                e.ConfigureConsumer<PackagePublishedConsumer>(ctx);
                e.ConfigureConsumer<PackagePublishFaultConsumer>(ctx);
                e.ConfigureConsumer<GetArtifactSignatureConsumer>(ctx);
                e.ConfigureConsumer<GetArtifactLocationConsumer>(ctx);
                e.ConfigureConsumer<RepositorySnapshotCompletedConsumer>(ctx);
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
    builder.Services.AddLuminaCoreReadiness<BuildDbContext>()
        .AddCheck<DockerReadinessHealthCheck>("docker-and-runners", tags: ["ready"], timeout: TimeSpan.FromSeconds(10))
        .AddConfiguredHttpReadiness("minio", "MinIO:Endpoint", "minio:9000", "/minio/health/ready")
        .AddWritableDirectoriesReadiness(
            "/app/builds",
            "/opt/lumina/builds",
            "/opt/lumina/sources",
            "/opt/lumina/extra-sources/pipelines");

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

    // Fail startup when the shared volumes are not writable by the service
    // identity. The volume-init Compose service establishes the shared GID and
    // setgid permissions; silently continuing here would only defer the failure
    // until a build is queued.
    foreach (var dir in new[] { "/app/builds", "/opt/lumina/builds", "/opt/lumina/sources", "/opt/lumina/extra-sources/pipelines" })
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probePath = Path.Combine(dir, $".lumina-write-probe-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probePath, "ok");
            File.Delete(probePath);
            Log.Information("Verified writable build volume: {Dir}", dir);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Required build volume '{dir}' is not writable by the BuildService identity.", ex);
        }
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
        app.UseDeveloperExceptionPage();
    }

    app.UseLuminaRequestCorrelation();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    app.MapLuminaHealthChecks();
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
