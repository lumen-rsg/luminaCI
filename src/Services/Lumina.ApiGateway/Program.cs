using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Lumina.ApiGateway.Data;
using Lumina.ApiGateway.Services;
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

    // Redis distributed cache — backs the refresh-token store (SEC-05).
    // The gateway already received Redis__ConnectionString in compose but never
    // registered the cache; the refresh/revocation machinery needs it.
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = builder.Configuration["Redis:ConnectionString"] ?? "redis:6379";
        options.InstanceName = "lumina:auth:";
    });
    builder.Services.AddSingleton<RefreshTokenStore>();
    builder.Services.AddSingleton(new CookieAuthHelper(builder.Configuration, jwtKey));

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

    // Cookie -> Bearer conversion (SEC-05).
    // The browser authenticates with the lumina_access HttpOnly cookie; the rest
    // of the pipeline (JWT validation, YARP forwarding, downstream SEC-04
    // re-validation) all speak "Authorization: Bearer". Promote the cookie to a
    // header here so the shared JWT wiring stays untouched. A request carrying an
    // explicit Authorization header (e.g. an API client) wins, so this only ever
    // fills in the gap for browser traffic.
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Headers.Authorization.Count == 0 &&
            ctx.Request.Cookies.TryGetValue(CookieAuthHelper.AccessCookie, out var cookieToken) &&
            !string.IsNullOrWhiteSpace(cookieToken))
        {
            ctx.Request.Headers.Authorization = "Bearer " + cookieToken;
        }
        await next();
    });

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapReverseProxy();
    app.MapHealthChecks("/health");

    // Login endpoint — verifies against the DB credential store and issues:
    //   • a short-lived access JWT inside the lumina_access HttpOnly cookie, and
    //   • a revocable refresh token inside the lumina_refresh HttpOnly cookie.
    // The raw token is NEVER returned in the response body, so client-side JS
    // (and therefore XSS) cannot read it (SEC-05).
    app.MapPost("/api/auth/login", async (
        LoginRequest request,
        HttpContext ctx,
        AuthDbContext db,
        IConfiguration config,
        CookieAuthHelper cookies,
        RefreshTokenStore refreshStore) =>
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

        // Issue access + refresh, both HttpOnly/SameSite=Strict cookies.
        var (accessJwt, accessExpiresUtc) = cookies.IssueAccessToken(user);
        var refreshLifetime = TimeSpan.FromHours(int.TryParse(config["Jwt:RefreshHours"], out var rh) ? rh : 8);
        var (refreshToken, refreshRecord) = await refreshStore.IssueAsync(user, refreshLifetime);

        cookies.SetAccessCookie(ctx.Response, accessJwt, accessExpiresUtc);
        cookies.SetRefreshCookie(ctx.Response, refreshToken, refreshRecord.ExpiresAt);

        return Results.Ok(new
        {
            username = user.Username,
            role = user.Role,
            expires = accessExpiresUtc
        });
    })
    .AllowAnonymous()
    .RequireRateLimiting("auth-login");

    // Refresh endpoint — exchanges a valid refresh cookie for a new access cookie
    // (and rotates the refresh token). Called by the WASM client when the access
    // token has expired, or proactively on app startup.
    app.MapPost("/api/auth/refresh", async (
        HttpContext ctx,
        IConfiguration config,
        CookieAuthHelper cookies,
        RefreshTokenStore refreshStore) =>
    {
        if (!ctx.Request.Cookies.TryGetValue(CookieAuthHelper.RefreshCookie, out var refreshToken) ||
            string.IsNullOrWhiteSpace(refreshToken))
        {
            return Results.Json(new { error = "No refresh token" }, statusCode: 401);
        }

        var refreshLifetime = TimeSpan.FromHours(int.TryParse(config["Jwt:RefreshHours"], out var rh) ? rh : 8);
        var rotated = await refreshStore.RotateAsync(refreshToken, refreshLifetime);
        if (rotated is null)
        {
            // Invalid/expired/revoked — clear the stale cookies.
            cookies.ClearCookies(ctx.Response);
            return Results.Json(new { error = "Invalid refresh token" }, statusCode: 401);
        }

        var (newRefresh, record) = rotated.Value;

        // Reconstruct a User purely to sign a fresh access JWT.
        var user = new User
        {
            Id = record.UserId,
            Username = record.Username,
            Role = record.Role
        };
        var (accessJwt, accessExpiresUtc) = cookies.IssueAccessToken(user);
        cookies.SetAccessCookie(ctx.Response, accessJwt, accessExpiresUtc);
        cookies.SetRefreshCookie(ctx.Response, newRefresh, record.ExpiresAt);

        return Results.Ok(new
        {
            username = record.Username,
            role = record.Role,
            expires = accessExpiresUtc
        });
    })
    .AllowAnonymous();

    // Logout endpoint — revokes the refresh token server-side and clears cookies.
    app.MapPost("/api/auth/logout", async (HttpContext ctx, RefreshTokenStore refreshStore, CookieAuthHelper cookies) =>
    {
        if (ctx.Request.Cookies.TryGetValue(CookieAuthHelper.RefreshCookie, out var refreshToken))
        {
            await refreshStore.RevokeAsync(refreshToken);
        }
        cookies.ClearCookies(ctx.Response);
        return Results.Ok(new { ok = true });
    })
    .AllowAnonymous();

    // /me — lets the WASM client learn auth state without ever reading the
    // cookie. Returns 200 { username, role } when the access cookie is valid
    // (cookie->bearer promotion above makes the user available here), 401
    // otherwise (the client then tries /refresh).
    app.MapGet("/api/auth/me", (HttpContext ctx) =>
    {
        var user = ctx.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }
        return Results.Ok(new
        {
            username = user.Identity.Name,
            role = user.FindFirst(ClaimTypes.Role)?.Value
        });
    })
    .RequireAuthorization("default");

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