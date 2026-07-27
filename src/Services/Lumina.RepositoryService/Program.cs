using Lumina.RepositoryService.Consumers;
using Lumina.RepositoryService.Data;
using Lumina.RepositoryService.Services;
using Lumina.Shared.Extensions;
using Lumina.Web.Shared;
using Lumina.Web.Shared.Health;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Minio;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Lumina Repository Service");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console());

    builder.Services.AddDbContext<RepositoryDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

    builder.Services.AddMinio(client => client
        .WithEndpoint(builder.Configuration["Minio:Endpoint"] ?? "minio:9000")
        .WithCredentials(
            builder.Configuration["Minio:AccessKey"] ?? throw new InvalidOperationException("Minio:AccessKey not configured"),
            builder.Configuration["Minio:SecretKey"] ?? throw new InvalidOperationException("Minio:SecretKey not configured"))
        // MinIO is private to the Compose network and serves plaintext on 9000;
        // making this explicit also gives presigned URLs the correct scheme.
        .WithSSL(false)
        .Build());

    builder.Services.AddScoped<RepositoryManagerService>();
    builder.Services.AddScoped<MinioStorageService>();
    builder.Services.AddScoped<SignatureVerificationService>();
    builder.Services.AddHttpClient("ArtifactStorage", client =>
        client.Timeout = TimeSpan.FromMinutes(2));

    // Redis distributed cache
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = builder.Configuration["Redis:ConnectionString"] ?? "redis:6379";
        options.InstanceName = "lumina:repository:";
    });
    builder.Services.AddSingleton<RedisCacheService>();

    builder.Services.AddMassTransit(x =>
    {
        x.ConfigureHealthCheckOptions(options => options.Tags.Add("ready"));
        x.AddConsumer<PackagePublishRequestedConsumer>();
        x.AddEntityFrameworkOutbox<RepositoryDbContext>(outbox =>
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

            cfg.ReceiveEndpoint("lumina-repository-service", endpoint =>
            {
                // Publication owns an explicit DB/filesystem transaction. An EF
                // consumer outbox would start another transaction on the same
                // DbContext; buffer PackagePublished in memory until the
                // publication transaction succeeds instead.
                endpoint.UseInMemoryOutbox(ctx);
                endpoint.ConfigureConsumer<PackagePublishRequestedConsumer>(ctx);
            });

            cfg.UseMessageRetry(r => r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)));
        });
    });

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        });
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddLuminaJwtAuthentication(builder.Configuration);
    builder.Services.AddLuminaAuthorization();
    builder.Services.AddLuminaCoreReadiness<RepositoryDbContext>()
        .AddConfiguredHttpReadiness("minio", "Minio:Endpoint", "minio:9000", "/minio/health/ready")
        .AddWritableDirectoriesReadiness(
            builder.Configuration["Repository:BasePath"] ?? "/app/repos");

    // Allow large file uploads (up to 500MB)
    builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
    {
        options.MultipartBodyLengthLimit = 500 * 1024 * 1024;
    });
    builder.WebHost.ConfigureKestrel(serverOptions =>
    {
        serverOptions.Limits.MaxRequestBodySize = 500 * 1024 * 1024;
    });

    var app = builder.Build();

    // Apply EF Core migrations (fail-closed). Errors propagate to the top-level
    // handler rather than being swallowed as "tables may already exist".
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<RepositoryDbContext>();
        await DatabaseInitializer.MigrateAsync(db);
        Log.Information("Repository database schema applied (EF Core migrations)");
        var storage = scope.ServiceProvider.GetRequiredService<MinioStorageService>();
        await storage.RecoverInterruptedPublicationsAsync();
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
    Log.Fatal(ex, "Repository Service terminated unexpectedly");
    // Propagate so the host exits with a non-zero code. Swallowing here would
    // make a crash look like a clean exit (code 0), so Docker's restart policy
    // could not tell them apart (SEC-024). The finally below still flushes logs.
    throw;
}
finally
{
    Log.CloseAndFlush();
}
