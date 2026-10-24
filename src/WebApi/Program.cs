using System.Threading.RateLimiting;
using Application;
using Application.Interfaces;
using Infrastructure;
using Microsoft.AspNetCore.OData;
using WebApi.Authentication;
using WebApi.Controllers;
using WebApi.Endpoints;
using WebApi.ErrorHandling;
using WebApi.OData;

var builder = WebApplication.CreateBuilder(args);
// OData only parses and applies query options (see ODataQueryExecutor); there are no OData routes or $metadata.
// Select/Expand/Apply stay off here and are rejected by ODataQueryExecutor as well.
builder.Services.AddControllers().AddOData(options =>
    options.Filter().OrderBy().Count().SetMaxTop(ODataQueryExecutor.MaxTop));
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();

builder.Services.AddDataProtection();
builder.Services.AddApplicationServices();
builder.Services.AddApiOnlyApplicationServices();

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
builder.Services.AddCollectorHealthClient();

// The audit log names the signed-in user; this replaces the collector's default ("system") that the line above registers.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

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

app.UseExceptionHandler();

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
