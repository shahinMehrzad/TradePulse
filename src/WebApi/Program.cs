using Microsoft.AspNetCore.DataProtection;
using System.Threading.RateLimiting;
using Application;
using Application.Interfaces;
using Infrastructure;
using Infrastructure.Exchange.Binance;
using Microsoft.AspNetCore.OData;
using WebApi.Authentication;
using WebApi.Controllers;
using WebApi.Endpoints;
using WebApi.ErrorHandling;
using WebApi.Hosting;
using WebApi.OData;

var builder = WebApplication.CreateBuilder(args);
// OData only parses and applies query options (see ODataQueryExecutor); there are no OData routes or $metadata.
// Select/Expand/Apply stay off here and are rejected by ODataQueryExecutor as well.
builder.Services.AddControllers().AddOData(options =>
    options.Filter().OrderBy().Count().SetMaxTop(ODataQueryExecutor.MaxTop));
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();

// Keys of data protection (the tokens of password resets, ...) survive a restart when a folder is given; the deploy stack mounts a volume there.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("TradePulse.WebApi");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
builder.Services.AddProxyHeaders();
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
builder.Services.AddBinanceSymbolCatalog();
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

// First, so that everything after it (the rate limiter, the audit trail) sees the real client address.
app.UseForwardedHeaders();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

await Infrastructure.DependencyInjection.SeedAsync(app.Services, app.Configuration);

// `WebApi.dll reset-access <user name or email>`: the way back in for somebody with access to the server, then exit.
if (args is ["reset-access", var who])
    return await AccessRecovery.RunAsync(app.Services, who);

app.UseHttpsRedirection();

if (corsOrigins.Length > 0)
    app.UseCors();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapCandleEndpoints();

app.Run();
return 0;

// Lets WebApplicationFactory<Program> reach the entry point from the integration tests.
public partial class Program;
