using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Lumina.ApiGateway.Data;
using Lumina.ApiGateway.Services;
using Lumina.Shared.Models;
using Lumina.Web.Shared;
using Lumina.Web.Shared.Authorization;
using Lumina.Web.Shared.Health;
using Microsoft.AspNetCore.HttpOverrides;
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

    // Request body limits.
    //
    // Kestrel's MaxRequestBodySize and FormOptions.MultipartBodyLengthLimit are
    // process-wide; setting them to long.MaxValue (as this previously did) opens
    // EVERY route to unbounded bodies — so /api/auth/login, /api/webhooks, etc.
    // could be OOM'd with no valid credential. Instead we set a generous process
    // default (well above any legitimate JSON) and then raise the limit only for
    // the two genuine upload routes in the pipeline below (SEC-015).
    //
    //   • /api/repository/upload    — package uploads (capped to 500 MiB at the
    //     downstream RepositoryService, so mirror that here rather than infinity)
    //   • /api/extra-sources/...    — extra build sources (unbounded downstream)
    //
    // Every other route is bounded by the Kestrel default below.
    const long DefaultMaxRequestBodySize = 64 * 1024 * 1024; // 64 MiB process-wide ceiling
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxRequestBodySize = DefaultMaxRequestBodySize;
        options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(30);
        options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(30);
    });

    // YARP Reverse Proxy
    builder.Services.AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

    // ForwardedHeaders — the gateway always sits behind exactly one trusted
    // proxy hop (nginx for browser traffic, optionally the host port-forward).
    // Without this, Connection.RemoteIpAddress is the proxy's address and every
    // IP-keyed rate-limit bucket collapses to a single "nginx" entry, making
    // the per-IP limiters useless (SEC-012 follow-up). The default
    // KnownNetworks/KnownProxies are loopback ranges only; in compose the proxy
    // reaches us from the docker bridge, so clear both and let the deployment
    // network boundary (only :5000 published) be the trust boundary. This is
    // the documented pattern for a single-hop proxy: a direct external client
    // cannot inject an X-Forwarded-For header that is honored.
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
    });

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new() { Title = "Lumina CI API Gateway", Version = "v1" });
    });

    // Rate limiting (SEC-012 follow-up).
    //
    // Two layers:
    //   • Global token bucket per client IP — caps overall request volume so a
    //     single host cannot flood the gateway or the proxied services. A token
    //     bucket (vs. the old fixed window) lets a legitimate client issue a
    //     short burst of list/SSE calls without being throttled at the doorstep
    //     of each window, yet still bounds sustained rate.
    //   • "auth-login" policy — a strict per-IP AND per-username bucket on
    //     /api/auth/login. Keying on username too means a distributed attacker
    //     from many IPs still cannot pile onto one account, and one infected IP
    //     password-spraying many accounts is bounded per-account. The username
    //     is read from the request body because the policy runs before endpoint
    //     model binding.
    //
    // All thresholds come from configuration so they can be tuned per
    // deployment without a rebuild (RateLimit:* section in appsettings).
    var rlConfig = builder.Configuration.GetSection("RateLimit");
    var globalPermit = rlConfig.GetValue("Global:PermitLimit", 100);
    var globalTokensPerSec = rlConfig.GetValue("Global:TokensPerSecond", 100);
    var globalQueue = rlConfig.GetValue("Global:QueueLimit", 10);
    var loginPermit = rlConfig.GetValue("Login:PermitLimit", 5);
    var loginTokensPerSec = rlConfig.GetValue("Login:TokensPerSecond", 5);
    var loginQueue = rlConfig.GetValue("Login:QueueLimit", 0);

    builder.Services.AddRateLimiter(options =>
    {
        // Global token bucket per real client IP. RemoteIpAddress already
        // reflects the end user once ForwardedHeaders is applied below.
        options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return System.Threading.RateLimiting.RateLimitPartition.GetTokenBucketLimiter(ip,
                _ => new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
                {
                    TokenLimit = globalPermit,
                    TokensPerPeriod = globalTokensPerSec,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                    QueueLimit = globalQueue
                });
        });

        // Strict login policy — bucketed per (IP, username). The username is
        // pulled from the JSON body; on parse failure / missing field the
        // bucket keys on the raw IP alone, so a malformed request is still
        // bounded. Buffering + rewind lets endpoint binding re-read the body.
        options.AddPolicy("auth-login", context =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var username = TryReadUsername(context) ?? "";
            var partitionKey = string.IsNullOrEmpty(username) ? $"ip:{ip}" : $"ip:{ip}|u:{username.ToLowerInvariant()}";
            return System.Threading.RateLimiting.RateLimitPartition.GetTokenBucketLimiter(partitionKey,
                _ => new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
                {
                    TokenLimit = loginPermit,
                    TokensPerPeriod = loginTokensPerSec,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                    QueueLimit = loginQueue
                });
        });

        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.StatusCode = 429;
            await context.HttpContext.Response.WriteAsync("Too many requests. Please try again later.", ct);
        };
    });

    builder.Services.AddLuminaCoreReadiness<AuthDbContext>()
        .AddConfiguredHttpReadiness("build-service", "Services:BuildService", "http://build-service:5001", "/health/ready")
        .AddConfiguredHttpReadiness("security-service", "Services:SecurityService", "http://security-service:5002", "/health/ready")
        .AddConfiguredHttpReadiness("scanner-service", "Services:ScannerService", "http://scanner-service:5003", "/health/ready")
        .AddConfiguredHttpReadiness("repository-service", "Services:RepositoryService", "http://repository-service:5004", "/health/ready")
        .AddConfiguredHttpReadiness("source-service", "Services:SourceService", "http://source-service:5006", "/health/ready");

    var app = builder.Build();

    // Apply EF Core migrations (fail-closed), then seed the initial admin.
    // Migration errors propagate to the top-level handler rather than being
    // swallowed as "tables may already exist".
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await DatabaseInitializer.MigrateAsync(db);
        Log.Information("Auth database schema applied (EF Core migrations)");
        await SeedUsersAsync(db, builder.Configuration);
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    // Apply ForwardedHeaders BEFORE rate limiting and auth so that every
    // downstream component (limiter partitions, logging, YARP forwarding) sees
    // the real client IP. Must run early — before anything that consumes
    // Connection.RemoteIpAddress / scheme (SEC-012 follow-up).
    app.UseForwardedHeaders();

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

    // Per-route request body limits (SEC-015 follow-up).
    //
    // The Kestrel process-wide MaxRequestBodySize is a transport-level ceiling
    // applied to every connection. We set it to a modest default (above) so that
    // non-upload routes (auth, webhooks, build control...) reject oversized
    // bodies at the door. The genuine upload routes need more, so here we raise
    // the per-request limit ONLY for those paths, before YARP forwards them.
    //
    // IRequestBodySizeFeature.MaxRequestBodySize is nullable: null means "no
    // limit", a value caps it. Setting it here overrides the Kestrel default for
    // just this request without affecting any other route.
    //
    // /api/repository/upload mirrors the downstream RepositoryService 500 MiB
    // cap (going higher here would just let bytes through to be rejected later).
    // /api/extra-sources/... is unbounded downstream, so it gets long.MaxValue.
    app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        var feature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (feature is not null && !feature.IsReadOnly)
        {
            if (path.StartsWith("/api/extra-sources/", StringComparison.OrdinalIgnoreCase))
            {
                feature.MaxRequestBodySize = long.MaxValue; // extra build sources
            }
            else if (path.StartsWith("/api/repository/", StringComparison.OrdinalIgnoreCase) &&
                     path.EndsWith("/upload", StringComparison.OrdinalIgnoreCase))
            {
                feature.MaxRequestBodySize = 500L * 1024 * 1024; // 500 MiB, mirrors downstream
            }
            // else: keep the Kestrel default (DefaultMaxRequestBodySize above)
        }
        await next();
    });

    app.MapReverseProxy();
    app.MapLuminaHealthChecks();

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
        var baseLockoutMinutes = int.TryParse(config["Auth:LockoutMinutes"], out var lm) ? lm : 15;
        // Cap the exponential backoff so a misbehaving client can't permanently
        // lock an account (denial of its own legitimate owner). 24h ceiling.
        var maxLockoutMinutes = int.TryParse(config["Auth:MaxLockoutMinutes"], out var mlm) ? mlm : 1440;

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= maxFailedLogins)
            {
                // Exponential backoff: each successive lockout cycle doubles the
                // penalty (base * 2^count), capped at MaxLockoutMinutes. count is
                // NOT reset on lockout, so the backoff compounds across attempts;
                // it is cleared only on a successful login.
                var penalty = (long)Math.Min(
                    maxLockoutMinutes,
                    baseLockoutMinutes * Math.Pow(2, user.LockoutCount));
                user.LockoutUntil = DateTimeOffset.UtcNow.AddMinutes(penalty);
                user.FailedLoginAttempts = 0;
                user.LockoutCount++;
            }
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);
        }

        // Success — reset the failure counter and clear the backoff state
        user.FailedLoginAttempts = 0;
        user.LockoutUntil = null;
        user.LockoutCount = 0;
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
    .RequireAuthorization(AuthPolicies.Default);

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "API Gateway terminated unexpectedly");
    // Propagate so the host exits with a non-zero code. Swallowing here would
    // make a crash look like a clean exit (code 0), so Docker's restart policy
    // could not tell them apart (SEC-024). The finally below still flushes logs.
    throw;
}
finally
{
    Log.CloseAndFlush();
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

/// <summary>
/// Reads the <c>username</c> field from the login JSON body for the rate-limiter
/// partitioner, which runs before endpoint model binding. Buffers and rewinds the
/// request stream so the downstream <c>LoginRequest</c> binding still sees the
/// full body. Returns <c>null</c> on any parse problem — the limiter then keys
/// on IP alone, so a malformed request stays bounded.
/// </summary>
static string? TryReadUsername(HttpContext context)
{
    try
    {
        if (!context.Request.ContentLength.HasValue || context.Request.ContentLength == 0)
            return null;

        // The body is forward-only by default; enable buffering so the endpoint
        // can re-read it after the limiter has consumed it here.
        context.Request.EnableBuffering();
        context.Request.Body.Position = 0;
        using var reader = new StreamReader(
            context.Request.Body,
            leaveOpen: true);
        var body = reader.ReadToEnd();
        context.Request.Body.Position = 0;

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.ValueKind == JsonValueKind.Object &&
               doc.RootElement.TryGetProperty("username", out var u) &&
               u.ValueKind == JsonValueKind.String
            ? u.GetString()
            : null;
    }
    catch
    {
        // Any failure → fall back to IP-only bucketing. Never let the limiter
        // itself throw and turn a brute-force attempt into a 500.
        return null;
    }
}

public record LoginRequest(string Username, string Password);
