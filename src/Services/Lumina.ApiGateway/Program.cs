using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

    // JWT Authentication
    var jwtSecret = builder.Configuration["Jwt:Secret"]
        ?? throw new InvalidOperationException("Jwt:Secret is not configured. Set it via environment variable or configuration.");
    var jwtKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "LuminaCI",
                ValidAudience = builder.Configuration["Jwt:Audience"] ?? "LuminaCI",
                IssuerSigningKey = jwtKey,
                ClockSkew = TimeSpan.Zero
            };
        });

    builder.Services.AddAuthorization();

    // YARP Reverse Proxy
    builder.Services.AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new() { Title = "Lumina CI API Gateway", Version = "v1" });
    });

    // Rate limiting — 100 requests/minute per IP
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
        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.StatusCode = 429;
            await context.HttpContext.Response.WriteAsync("Too many requests. Please try again later.", ct);
        };
    });

    builder.Services.AddHealthChecks();

    var app = builder.Build();

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

    // Login endpoint — generates real JWT
    app.MapPost("/api/auth/login", (LoginRequest request, IConfiguration config) =>
    {
        if ((request.Username == "admin" && request.Password == "admin") ||
            (request.Username == "developer" && request.Password == "developer"))
        {
            var issuer = config["Jwt:Issuer"] ?? "LuminaCI";
            var audience = config["Jwt:Audience"] ?? "LuminaCI";
            var expiryHours = int.TryParse(config["Jwt:ExpiryHours"], out var h) ? h : 8;

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, request.Username),
                new(ClaimTypes.Role, request.Username == "admin" ? "Admin" : "Developer"),
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
                username = request.Username,
                role = request.Username == "admin" ? "Admin" : "Developer"
            });
        }
        return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);
    }).AllowAnonymous();

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

public record LoginRequest(string Username, string Password);