using System.Security.Claims;
using System.Threading.RateLimiting;
using Domain.Entities;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Npgsql;
using Trader.Audit;
using Trader.Binance;
using Trader.Security;
using Trader.Trading;
using Trader.Vault;

// The only program that holds the private keys of the exchange keys. It has no public port: the admin panel reaches it over the
// internal network of the stack, with the signed-in user's own access token (and, for anything that changes a key, the
// confirmation token of a second-factor code), and it answers for that user's keys only.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTokenSecurity(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ElevationValidator>();
builder.Services.AddSingleton<RequireElevationFilter>();

builder.Services.AddOptions<VaultOptions>().BindConfiguration(VaultOptions.SectionName)
    .Validate(o => TryParseKey(o.MasterKey), "Vault:MasterKey must be 32 random bytes in base64 (e.g. `openssl rand -base64 32`).")
    .ValidateOnStart();
builder.Services.Configure<BinanceOptions>(builder.Configuration.GetSection(BinanceOptions.SectionName));
builder.Services.AddOptions<TradingOptions>().BindConfiguration(TradingOptions.SectionName)
    .Validate(o => o.MaxOrderUsd > 0 && o.MaxDailyUsd >= o.MaxOrderUsd, "Trading:MaxDailyUsd must be at least Trading:MaxOrderUsd, and both above 0.")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("Vault")
    ?? throw new InvalidOperationException("Connection string 'Vault' is not set (environment variable ConnectionStrings__Vault).");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<VaultCrypto>();
builder.Services.AddSingleton<ExchangeKeyRepository>();
builder.Services.AddSingleton<TradingRepository>();
builder.Services.AddSingleton<AuditWriter>();
builder.Services.AddSingleton<BinanceClient>();
builder.Services.AddSingleton<KeyVerifier>();

// No default logging of requests: the address of a signed Binance request holds the signature. The client keeps its own logs off.
builder.Services.AddHttpClient(BinanceClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15)).RemoveAllLoggers();

// Making a key pair (4096-bit RSA) and asking Binance are the costly things: a few a minute per user is plenty.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("keys", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimiting:KeysPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("orders", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimiting:OrdersPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

await ExchangeKeyRepository.EnsureSchemaAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), CancellationToken.None);
await TradingRepository.EnsureSchemaAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), CancellationToken.None);

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok("ok")).AllowAnonymous();
app.MapExchangeKeys();
app.MapTrading();

app.Run();

static bool TryParseKey(string key)
{
    try
    {
        VaultCrypto.ParseKey(key);
        return true;
    }
    catch (InvalidOperationException)
    {
        return false;
    }
}

// Lets WebApplicationFactory<Program> reach the entry point from the integration tests.
public partial class Program;
