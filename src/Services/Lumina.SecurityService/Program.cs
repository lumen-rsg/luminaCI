using Lumina.SecurityService.Consumers;
using Lumina.SecurityService.Data;
using Lumina.SecurityService.Services;
using Lumina.Shared.Extensions;
using Lumina.Web.Shared;
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
        x.AddConsumer<HashStoreRequestedConsumer>();
        x.AddConsumer<PackageSigningRequestedConsumer>();
        x.AddConsumer<GetActiveSigningKeyConsumer>();
        x.AddConsumer<GetActivePublicKeyConsumer>();

        x.UsingRabbitMq((ctx, cfg) =>
        {
            cfg.Host(builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq", "/", h =>
            {
                h.Username(builder.Configuration["RabbitMQ:Username"] ?? throw new InvalidOperationException("RabbitMQ:Username not configured"));
                h.Password(builder.Configuration["RabbitMQ:Password"] ?? throw new InvalidOperationException("RabbitMQ:Password not configured"));
            });

            cfg.ReceiveEndpoint("lumina-security-service", e =>
            {
                e.ConfigureConsumer<HashStoreRequestedConsumer>(ctx);
                e.ConfigureConsumer<PackageSigningRequestedConsumer>(ctx);
                e.ConfigureConsumer<GetActiveSigningKeyConsumer>(ctx);
                e.ConfigureConsumer<GetActivePublicKeyConsumer>(ctx);
            });

            cfg.UseMessageRetry(r => r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)));
        });
    });

    builder.Services.AddControllers();
    builder.Services.AddLuminaJwtAuthentication(builder.Configuration);
    builder.Services.AddLuminaAuthorization();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddHealthChecks();

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<SecurityDbContext>();
        await CreateTablesWithScriptAsync(db);

        // Auto-generate a default PGP key if none exists
        var pgpService = scope.ServiceProvider.GetRequiredService<PgpSigningService>();
        var keys = await pgpService.ListKeysAsync();
        if (keys.Count == 0)
        {
            var passphrase = builder.Configuration["Gpg:Passphrase"]
                ?? throw new InvalidOperationException("Gpg:Passphrase is not configured. Set GPG_PASSPHRASE in the environment.");
            await pgpService.GenerateKeyAsync("Lumina CI", "lumina@ci.local", passphrase, "system");
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
    app.MapHealthChecks("/health");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Security Service terminated unexpectedly");
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