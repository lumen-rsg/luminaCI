using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Lumina.ApiGateway.Data;
using Lumina.Shared.Models;
using Lumina.Web.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Lumina API Gateway");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console());

    // JWT Authentication — shared with every downstream service so they can
    // independently re-validate the bearer token as defense-in-depth (SEC-04).
    // The secret is required here because the gateway both issues (login) and
    // validates tokens; downstream services only validate.
    var jwtSecret = builder.Configuration["Jwt:Secret"]
        ?? throw new InvalidOperationException("Jwt:Secret is not configured. Set it via environment variable or configuration.");
    var jwtKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

    builder.Services.AddLuminaJwtAuthentication(builder.Configuration);
    builder.Services.AddLuminaAuthorization();

    // User credential store (Postgres)
    builder.Services.AddDbContext<AuthDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

    // Allow unlimited file uploads (extra sources can be large)
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

    // YARP Reverse Proxy
    builder.Services.AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new() { Title = "Lumina CI API Gateway", Version = "v1" });
    });

    // Rate limiting — 100 requests/minute per IP (global), stricter policy for the login form
    builder.Services.AddRateLimiter(options =>
    {
        options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(ip,
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                    QueueLimit = 10
                });
        });
        // Stricter limiter for the login endpoint: 10 attempts/minute per IP, no queue.
        options.AddPolicy("auth-login", context =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(ip,
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                });
        });
        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.StatusCode = 429;
            await context.HttpContext.Response.WriteAsync("Too many requests. Please try again later.", ct);
        };
    });

    builder.Services.AddHealthChecks();

    var app = builder.Build();

    // Create the auth schema/tables and seed the initial admin (idempotent, like the other services)
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await CreateTablesWithScriptAsync(db);
        await SeedUsersAsync(db, builder.Configuration);
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapReverseProxy();
    app.MapHealthChecks("/health");

    // Login endpoint — verifies against the DB credential store and issues a JWT
    app.MapPost("/api/auth/login", async (LoginRequest request, AuthDbContext db, IConfiguration config) =>
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == request.Username);

        if (user is null || !user.IsActive)
        {
            return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);
        }

        // Lockout: refuse while a lockout window is active
        if (user.LockoutUntil is not null && user.LockoutUntil > DateTimeOffset.UtcNow)
        {
            return Results.Json(new { error = "Account temporarily locked. Try again later." }, statusCode: 401);
        }

        var maxFailedLogins = int.TryParse(config["Auth:MaxFailedLogins"], out var m) ? m : 5;
        var lockoutMinutes = int.TryParse(config["Auth:LockoutMinutes"], out var lm) ? lm : 15;

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= maxFailedLogins)
            {
                user.LockoutUntil = DateTimeOffset.UtcNow.AddMinutes(lockoutMinutes);
                user.FailedLoginAttempts = 0;
            }
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);
        }

        // Success — reset the failure counter
        user.FailedLoginAttempts = 0;
        user.LockoutUntil = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var issuer = config["Jwt:Issuer"] ?? "LuminaCI";
        var audience = config["Jwt:Audience"] ?? "LuminaCI";
        var expiryHours = int.TryParse(config["Jwt:ExpiryHours"], out var h) ? h : 8;

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var credentials = new SigningCredentials(jwtKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(expiryHours),
            signingCredentials: credentials
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        return Results.Ok(new
        {
            token = tokenString,
            expires = token.ValidTo,
            username = user.Username,
            role = user.Role
        });
    })
    .AllowAnonymous()
    .RequireRateLimiting("auth-login");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "API Gateway terminated unexpectedly");
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

// Seeds the initial admin (and optional developer) account when the users table is empty.
// The admin password MUST be provided via Auth:AdminPassword — there are no default credentials.
static async Task SeedUsersAsync(AuthDbContext db, IConfiguration config)
{
    if (await db.Users.AnyAsync())
    {
        return;
    }

    var adminPassword = config["Auth:AdminPassword"];
    if (string.IsNullOrWhiteSpace(adminPassword))
    {
        throw new InvalidOperationException(
            "Auth:AdminPassword is not configured. Set ADMIN_PASSWORD to seed the initial admin account.");
    }

    var adminUsername = config["Auth:AdminUsername"] ?? "admin";

    db.Users.Add(new User
    {
        Id = Guid.NewGuid(),
        Username = adminUsername,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPassword),
        Role = "Admin",
        IsActive = true,
        FailedLoginAttempts = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    });
    Log.Information("Seeded initial admin account '{Admin}'", adminUsername);

    var developerPassword = config["Auth:DeveloperPassword"];
    if (!string.IsNullOrWhiteSpace(developerPassword))
    {
        var developerUsername = config["Auth:DeveloperUsername"] ?? "developer";
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = developerUsername,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(developerPassword),
            Role = "Developer",
            IsActive = true,
            FailedLoginAttempts = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        Log.Information("Seeded initial developer account '{Developer}'", developerUsername);
    }

    await db.SaveChangesAsync();
}

public record LoginRequest(string Username, string Password);