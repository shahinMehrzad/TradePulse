using System.Threading.RateLimiting;
using Application;
using Infrastructure;
using WebApi.Authentication;
using WebApi.Controllers;
using WebApi.Endpoints;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddDataProtection();
builder.Services.AddApplicationServices();

// Candle reads go through a HybridCache (memory, plus Redis when a connection string is configured).
if (builder.Configuration.GetValue("CandleCache:Enabled", true))
{
    var redis = builder.Configuration.GetConnectionString("Redis");
    if (!string.IsNullOrWhiteSpace(redis))
        builder.Services.AddStackExchangeRedisCache(options => options.Configuration = redis);

    builder.Services.AddCandleCaching(builder.Configuration);
}

builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddAuthInfrastructure(builder.Configuration);
builder.Services.AddInfrastructureServices(builder.Configuration);

// Login is the one anonymous door to the admin account: throttle it per client IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthController.LoginRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("RateLimiting:LoginPermitsPerMinute", 10),
                Window = TimeSpan.FromMinutes(1)
            }));
});

// CORS is off unless origins are listed. The panel is a server-side client and needs none; this is for a future browser client.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

await Infrastructure.DependencyInjection.SeedAsync(app.Services, app.Configuration);

app.UseHttpsRedirection();

if (corsOrigins.Length > 0)
    app.UseCors();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapCandleEndpoints();

app.Run();

// Lets WebApplicationFactory<Program> reach the entry point from the integration tests.
public partial class Program;
