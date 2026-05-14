using Lumina.ScannerService.Data;
using Lumina.ScannerService.Services;
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

    builder.Services.AddHttpClient<TrivyScannerService>();

    builder.Services.AddMassTransit(x =>
    {
        x.UsingRabbitMq((ctx, cfg) =>
        {
            cfg.Host(builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq", "/", h =>
            {
                h.Username(builder.Configuration["RabbitMQ:Username"] ?? throw new InvalidOperationException("RabbitMQ:Username not configured"));
                h.Password(builder.Configuration["RabbitMQ:Password"] ?? throw new InvalidOperationException("RabbitMQ:Password not configured"));
            });
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
