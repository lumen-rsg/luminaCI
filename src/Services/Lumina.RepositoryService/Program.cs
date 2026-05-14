using Lumina.RepositoryService.Data;
using Lumina.RepositoryService.Services;
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
            builder.Configuration["Minio:AccessKey"] ?? "lumina_minio",
            builder.Configuration["Minio:SecretKey"] ?? "lumina_minio_secret")
        .Build());

    builder.Services.AddScoped<RepositoryManagerService>();
    builder.Services.AddScoped<MinioStorageService>();

    builder.Services.AddMassTransit(x =>
    {
        x.UsingRabbitMq((ctx, cfg) =>
        {
            cfg.Host(builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq", "/", h =>
            {
                h.Username(builder.Configuration["RabbitMQ:Username"] ?? "lumina");
                h.Password(builder.Configuration["RabbitMQ:Password"] ?? "lumina_rmq_2024");
            });
        });
    });

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        });
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddHealthChecks();

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

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<RepositoryDbContext>();
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
    Log.Fatal(ex, "Repository Service terminated unexpectedly");
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
