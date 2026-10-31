using System.Net.Http.Headers;
using System.Net.Http.Json;
using Application.Exchange;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace WebApi.IntegrationTests;

/// <summary>
/// The real WebApi host (Program.cs, real middleware, real JWT validation) on top of a throwaway Postgres.
/// The database holds the auth schema (migrated and seeded by the app itself) and one small candle table.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string CandleUrl = "/api/candles/binance/futures/btcusdt/1h/latest?limit=5";

    // What the administrator is seeded with in these tests. The seeded password is temporary: the first sign-in has to
    // replace it and enrol two-factor, which AdminLoginAsync does once per fixture.
    public const string AdminEmail = "admin@example.com";
    public const string AdminPassword = "AdminPassword123!";
    public const string AdminChangedPassword = "AdminChangedPassword456!";

    /// <summary>The mails the API sends in these tests (it has no mail server): sign-in codes are read from here.</summary>
    public FakeMailSender Mail { get; } = new();

    private readonly SemaphoreSlim _adminSetup = new(1, 1);
    private string? _adminAuthenticatorKey;

    private record Step(string Stage, string? ChallengeToken);
    private record Enrollment(string SharedKey);

    /// <summary>The key the API signs its tokens with in these tests; tests that make a token of their own use it too.</summary>
    public const string JwtSecret = "signing-key-of-the-integration-tests-of-the-api-0123456789";

    /// <summary>The exchange's symbol list the API asks when a symbol is listed; no test reaches the real Binance.</summary>
    public FakeExchangeSymbolCatalog ExchangeCatalog { get; } = new();

    public string ConnectionString => _postgres.GetConnectionString();

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        var repository = new CandleRepository(_postgres.GetConnectionString());
        await repository.EnsureTableExistsAsync("binance_futures_btcusdt_1h", CancellationToken.None);

        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = Enumerable.Range(0, 10).Select(i =>
            new Candle(start.AddHours(i), 1m, 4m, 0.5m, 3m, 10m, start.AddHours(i + 1).AddMilliseconds(-1), 25m, 42, 6m, 15m));
        await repository.BulkInsert("binance_futures_btcusdt_1h", candles, CancellationToken.None);

        await ListSymbolsOnSpotAsync();
    }

    /// <summary>
    /// The migration lists the symbols on Binance Futures only. Some tests need a second market, so they are listed on Spot too, in
    /// the order that gives them the ids <see cref="ListingIds"/> expects. Straight into the database: signing in as the administrator
    /// here would use up the first sign-in that the first-run tests look at.
    /// </summary>
    private async Task ListSymbolsOnSpotAsync()
    {
        using var host = CreateClient();   // starts the host, which migrates the database
        var options = new DbContextOptionsBuilder<ConfigDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "config"))
            .Options;
        await using var context = new ConfigDbContext(options);

        foreach (var symbol in Enum.GetValues<SeededSymbol>())
        {
            var listing = new SymbolListing
            {
                ExchangeId = (int)ExchangeEnum.Binance, MarketId = (int)MarketTypeEnum.Spot, SymbolId = (int)symbol, ExchangeSymbol = symbol.ToString()
            };
            context.SymbolListings.Add(listing);
            await context.SaveChangesAsync();

            if (listing.Id != ListingIds.Of((int)ExchangeEnum.Binance, (int)MarketTypeEnum.Spot, (int)symbol))
                throw new InvalidOperationException($"{symbol} on Spot got listing id {listing.Id}, not the one ListingIds expects.");
        }
    }


    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var connectionString = _postgres.GetConnectionString();
        builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        builder.UseSetting("ConnectionStrings:AuthConnection", connectionString);
        builder.UseSetting("ConnectionStrings:ConfigConnection", connectionString);
        builder.UseSetting("ConnectionStrings:Redis", "");
        builder.UseSetting("JwtConfigurations:Secret", JwtSecret);
        builder.UseSetting("AdminConfigurations:Email", AdminEmail);
        builder.UseSetting("AdminConfigurations:Password", AdminPassword);
        // No collector runs next to the tests (the default address would find a real one on a developer machine).
        builder.UseSetting("Collector:HealthUrl", "");
        // Many tests log in within a minute; the throttle itself is tested with its own low limit.
        builder.UseSetting("RateLimiting:LoginPermitsPerMinute", "1000");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMailSender>();
            services.AddSingleton<IMailSender>(Mail);
        });
        builder.ConfigureTestServices(services => services.AddSingleton<IExchangeSymbolCatalog>(ExchangeCatalog));
    }

    /// <summary>
    /// Signs the seeded administrator in the way a real one has to: password, then the code of the authenticator app.
    /// The first call also does the one-time setup (new password, two-factor). Returns the answer of the last step, with the tokens.
    /// </summary>
    public async Task<HttpResponseMessage> AdminLoginAsync(HttpClient client, CancellationToken cancellationToken, string? device = null)
    {
        await EnsureAdminSetUpAsync(cancellationToken);

        var login = await PostAsync(client, "/api/auth/login",
            new { Email = AdminEmail, Username = "", Password = AdminChangedPassword }, device, cancellationToken);
        var step = (await login.Content.ReadFromJsonAsync<Step>(cancellationToken))!;

        return await PostAsync(client, "/api/auth/login/two-factor",
            new { step.ChallengeToken, Code = Totp.Code(_adminAuthenticatorKey!), IsRecoveryCode = false }, device, cancellationToken);
    }

    public const string NewUserPassword = "BrandNewPassword789!";

    public record TestAccount(string UserName, Guid Id, string AuthenticatorKey, string[] RecoveryCodes);

    /// <summary>A new administrator who has been through the whole first-time setup (password changed to <see cref="NewUserPassword"/>, two-factor on).</summary>
    public async Task<TestAccount> SetUpAdministratorAsync(string prefix, CancellationToken cancellationToken)
    {
        var admin = (await (await AdminLoginAsync(CreateClient(), cancellationToken)).Content.ReadFromJsonAsync<Tokens>(cancellationToken))!.AccessToken;
        var name = prefix + Guid.NewGuid().ToString("N")[..8];

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users") { Content = JsonContent.Create(new { UserName = name, Email = $"{name}@example.com" }) };
        create.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin);
        create.Headers.Add("X-Elevation", await AdminElevationAsync(admin, cancellationToken));
        var created = await (await CreateClient().SendAsync(create, cancellationToken)).Content.ReadFromJsonAsync<Created>(cancellationToken);

        using var client = CreateClient();
        var login = await PostAsync(client, "/api/auth/login", new { Email = "", Username = name, Password = created!.TemporaryPassword }, null, cancellationToken);
        var challenge = (await login.Content.ReadFromJsonAsync<Step>(cancellationToken))!.ChallengeToken;
        var changed = await PostAsync(client, "/api/auth/setup/password",
            new { ChallengeToken = challenge, CurrentPassword = created.TemporaryPassword, NewPassword = NewUserPassword }, null, cancellationToken);
        challenge = (await changed.Content.ReadFromJsonAsync<Step>(cancellationToken))!.ChallengeToken;
        var key = (await (await PostAsync(client, "/api/auth/setup/two-factor/begin", new { ChallengeToken = challenge }, null, cancellationToken))
            .Content.ReadFromJsonAsync<Enrollment>(cancellationToken))!.SharedKey;
        var done = await (await PostAsync(client, "/api/auth/setup/two-factor/confirm", new { ChallengeToken = challenge, Code = Totp.Code(key) }, null, cancellationToken))
            .Content.ReadFromJsonAsync<Tokens>(cancellationToken);

        return new TestAccount(name, created.UserId, key, done!.RecoveryCodes!);
    }

    /// <summary>Password and code, as a returning user signs in. Returns the answer of the last step, with the tokens.</summary>
    public async Task<HttpResponseMessage> LoginAsync(TestAccount account, string password, CancellationToken cancellationToken, string? device = null)
    {
        using var client = CreateClient();
        var login = await PostAsync(client, "/api/auth/login", new { Email = "", Username = account.UserName, Password = password }, device, cancellationToken);
        var step = (await login.Content.ReadFromJsonAsync<Step>(cancellationToken))!;
        return await PostAsync(client, "/api/auth/login/two-factor",
            new { step.ChallengeToken, Code = Totp.Code(account.AuthenticatorKey), IsRecoveryCode = false }, device, cancellationToken);
    }

    public record Tokens(string AccessToken, string RefreshToken, string[]? RecoveryCodes);
    private record Elevation(string Token);

    /// <summary>The proof of a fresh second-factor code that the sensitive actions ask for (<c>X-Elevation</c>), for the user with this authenticator key.</summary>
    public async Task<string> ElevationAsync(string accessToken, string authenticatorKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/step-up") { Content = JsonContent.Create(new { Code = Totp.Code(authenticatorKey) }) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var response = await CreateClient().SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Elevation>(cancellationToken))!.Token;
    }

    public async Task<string> AdminElevationAsync(string adminAccessToken, CancellationToken cancellationToken)
    {
        await EnsureAdminSetUpAsync(cancellationToken);
        return await ElevationAsync(adminAccessToken, _adminAuthenticatorKey!, cancellationToken);
    }
    private record Created(Guid UserId, string TemporaryPassword);

    private async Task EnsureAdminSetUpAsync(CancellationToken cancellationToken)
    {
        await _adminSetup.WaitAsync(cancellationToken);
        try
        {
            if (_adminAuthenticatorKey is not null)
                return;

            using var client = CreateClient();
            var login = await PostAsync(client, "/api/auth/login", new { Email = AdminEmail, Username = "", Password = AdminPassword }, null, cancellationToken);
            var challenge = (await login.Content.ReadFromJsonAsync<Step>(cancellationToken))!.ChallengeToken;

            var changed = await PostAsync(client, "/api/auth/setup/password",
                new { ChallengeToken = challenge, CurrentPassword = AdminPassword, NewPassword = AdminChangedPassword }, null, cancellationToken);
            challenge = (await changed.Content.ReadFromJsonAsync<Step>(cancellationToken))!.ChallengeToken;

            var begin = await PostAsync(client, "/api/auth/setup/two-factor/begin", new { ChallengeToken = challenge }, null, cancellationToken);
            var key = (await begin.Content.ReadFromJsonAsync<Enrollment>(cancellationToken))!.SharedKey;

            var confirm = await PostAsync(client, "/api/auth/setup/two-factor/confirm",
                new { ChallengeToken = challenge, Code = Totp.Code(key) }, null, cancellationToken);
            confirm.EnsureSuccessStatusCode();

            _adminAuthenticatorKey = key;
        }
        finally
        {
            _adminSetup.Release();
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body, string? device, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (device is not null)
            request.Headers.Add("X-Device-Id", device);

        var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response;
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
