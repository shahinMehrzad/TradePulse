using System.Security.Claims;
using System.Threading.RateLimiting;
using Domain.Entities;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Npgsql;
using Trader;
using Trader.Audit;
using Trader.Binance;
using Trader.Security;
using Trader.Trading;
using Trader.TradingCode;
using Trader.Vault;

// The only program that holds the private keys of the exchange keys. It has no public port: the admin panel reaches it over the
// internal network of the stack, with the signed-in user's own access token (and, for anything that changes a key, the
// confirmation token of a second-factor code), and it answers for that user's keys only.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTokenSecurity(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProxyHeaders();

// The trading code: a second factor that only this program checks (see TradingCodeEndpoints), and the security bot that hears about it.
builder.Services.AddSingleton<TradingCodeRepository>();
builder.Services.AddSingleton<TradingConfirmations>();
builder.Services.AddSingleton<RequireTradingConfirmationFilter>();
builder.Services.AddSingleton<TradingCodeService>();
builder.Services.AddSingleton<SecurityAlerts>();
builder.Services.AddSingleton<ISecurityAlerts>(provider => provider.GetRequiredService<SecurityAlerts>());
builder.Services.Configure<SecurityAlertsOptions>(builder.Configuration.GetSection(SecurityAlertsOptions.SectionName));
builder.Services.AddHttpClient(SecurityAlertSender.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://api.telegram.org/");
    client.Timeout = TimeSpan.FromSeconds(15);
}).RemoveAllLoggers(); // the bot token is part of the address
builder.Services.AddSingleton<SecurityAlertSender>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SecurityAlertSender>());

builder.Services.AddOptions<VaultOptions>().BindConfiguration(VaultOptions.SectionName)
    .Validate(o => TryParseKey(o.MasterKey), "Vault:MasterKey must be 32 random bytes in base64 (e.g. `openssl rand -base64 32`).")
    .ValidateOnStart();
builder.Services.Configure<BinanceOptions>(builder.Configuration.GetSection(BinanceOptions.SectionName));
builder.Services.AddOptions<TradingOptions>().BindConfiguration(TradingOptions.SectionName)
    .Validate(o => o.MaxOrderUsd > 0 && o.MaxDailyUsd >= o.MaxOrderUsd, "Trading:MaxDailyUsd must be at least Trading:MaxOrderUsd, and both above 0.")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("Vault")
    ?? throw new InvalidOperationException("Connection string 'Vault' is not set (environment variable ConnectionStrings__Vault).");
var vaultSource = NpgsqlDataSource.Create(connectionString);
builder.Services.AddSingleton(vaultSource);
// Audit log and the copy of the enabled symbols go to the main database (a role that may do only that). Not set: the vault's own database.
var mainConnectionString = builder.Configuration.GetConnectionString("Main");
builder.Services.AddSingleton(new MainDatabase(string.IsNullOrWhiteSpace(mainConnectionString) ? vaultSource : NpgsqlDataSource.Create(mainConnectionString)));
builder.Services.AddSingleton<TradedSymbolMirror>();
builder.Services.AddHostedService<TradedSymbolMirrorService>();
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
await TradingCodeRepository.EnsureSchemaAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), CancellationToken.None);

// `Trader.dll reset-trading-code <user id>` / `list-trading-codes`: the way back for somebody with a shell on the server, then exit.
if (args is ["reset-trading-code", var who])
    return await TradingCodeCli.ResetAsync(app.Services, who);
if (args is ["list-trading-codes"])
    return await TradingCodeCli.ListAsync(app.Services);

// First, so that the address of an alert is the visitor's, not the panel's.
app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok("ok")).AllowAnonymous();
app.MapExchangeKeys();
app.MapTrading();
app.MapTradingCode();

app.Run();
return 0;

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
