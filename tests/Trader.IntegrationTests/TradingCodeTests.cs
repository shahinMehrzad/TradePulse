using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Trader.Audit;
using Trader.TradingCode;
using Microsoft.AspNetCore.TestHost;
using TestTotp = WebApi.IntegrationTests.Totp;
using VaultTotp = Trader.TradingCode.Totp;

namespace Trader.IntegrationTests;

/// <summary>
/// The trading code: a second factor whose secret only the vault has. It decides who may touch the keys and the orders, so these tests are about
/// what it must hold even when the API says anything: no confirmation without the code, a code works once, wrong ones lock for a while and never for
/// good, the secret is not kept in the open, and the way back (recovery codes, the server's command) is only for the user and for the server.
/// </summary>
public class TradingCodeTests(TraderFixture trader) : IClassFixture<TraderFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeAlerts : ISecurityAlerts
    {
        private readonly List<SecurityAlert> _raised = [];
        public IReadOnlyList<SecurityAlert> Raised { get { lock (_raised) return [.. _raised]; } }
        public void Raise(SecurityAlert alert) { lock (_raised) _raised.Add(alert); }
    }

    private record SetupBody(string SharedKey, string AuthenticatorUri);
    private record EnrolledBody(string[] RecoveryCodes, string ConfirmationToken, DateTime ExpiresAt);
    private record ConfirmationBody(string Token, DateTime ExpiresAt);
    private record StatusBody(bool Enrolled, DateTime? LockedUntil, int RecoveryCodesLeft, DateTime? ConfirmedUntil);

    /// <summary>A host of the vault with its own clock and a place to read the alerts it raises.</summary>
    private (HttpClient Client, FakeTimeProvider Clock, FakeAlerts Alerts, IServiceProvider Services) Host()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var alerts = new FakeAlerts();
        var host = trader.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<ISecurityAlerts>();
            services.AddSingleton<ISecurityAlerts>(alerts);
        }));
        return (host.CreateClient(), clock, alerts, host.Services);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string accessToken, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, Ct);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, Ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>(Ct))!;

    private static string DetailOf(string json) => JsonDocument.Parse(json).RootElement.GetProperty("detail").GetString()!;

    /// <summary>An administrator the API signs in who has NOT set up a trading code yet.</summary>
    private (Guid Id, string Name, string AccessToken) FreshAdministrator(string name)
    {
        var id = Guid.NewGuid();
        var access = new Infrastructure.Identity.JwtAccessTokenService(Microsoft.Extensions.Options.Options.Create(TraderFixture.Jwt))
            .Create(new Application.Interfaces.AuthenticatedUser(id, name, $"{name}@example.com", [TraderFixture.AdminRole])).Token;
        return (id, name, access);
    }

    // ---- the setup

    [Fact]
    public async Task TheSetupGivesTheKeyThenTheRecoveryCodesOnceAndAConfirmation()
    {
        var (client, _, alerts, _) = Host();
        var (_, name, access) = FreshAdministrator("setup");

        var begin = await ReadAsync<SetupBody>(await PostAsync(client, "/trading-code/begin", access));
        Assert.Contains("TradePulse%20trading", begin.AuthenticatorUri);   // its own entry in the app, not mistaken for the sign-in's
        Assert.Contains($"secret={begin.SharedKey}", begin.AuthenticatorUri);

        var again = await ReadAsync<SetupBody>(await PostAsync(client, "/trading-code/begin", access));
        Assert.Equal(begin.SharedKey, again.SharedKey);                    // a reloaded page shows the key the app may already hold

        var wrong = await PostAsync(client, "/trading-code/setup", access, new { Code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var done = await PostAsync(client, "/trading-code/setup", access, new { Code = TestTotp.Code(begin.SharedKey) });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var enrolled = await ReadAsync<EnrolledBody>(done);
        Assert.Equal(10, enrolled.RecoveryCodes.Distinct().Count());
        Assert.All(enrolled.RecoveryCodes, code => Assert.Matches("^[A-Z2-9]{5}-[A-Z2-9]{5}$", code));
        Assert.NotEmpty(enrolled.ConfirmationToken);

        var status = await ReadAsync<StatusBody>(await GetAsync(client, "/trading-code", access));
        Assert.Equal((true, 10), (status.Enrolled, status.RecoveryCodesLeft));
        Assert.NotNull(status.ConfirmedUntil);

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(client, "/trading-code/begin", access)).StatusCode);   // a code that exists is not replaced here
        Assert.Contains(alerts.Raised, alert => alert.Title.Contains(name) && alert.Title.Contains("set up"));
    }

    [Fact]
    public async Task TheSecretAndTheRecoveryCodesAreNotKeptInTheOpen()
    {
        var (client, _, _, _) = Host();
        var (id, _, access) = FreshAdministrator("sealed");
        var begin = await ReadAsync<SetupBody>(await PostAsync(client, "/trading-code/begin", access));
        var enrolled = await ReadAsync<EnrolledBody>(await PostAsync(client, "/trading-code/setup", access, new { Code = TestTotp.Code(begin.SharedKey) }));

        await using var connection = new NpgsqlConnection(trader.ConnectionString);
        var row = await connection.QuerySingleAsync<(byte[] Secret, string Hashes)>("SELECT secret_encrypted, recovery_hashes FROM vault.trading_codes WHERE owner_user_id = @id", new { id });

        var secret = Base32Decode(begin.SharedKey);
        Assert.False(row.Secret.AsSpan().IndexOf(secret) >= 0);               // encrypted with the master key, which is not in the database
        Assert.All(enrolled.RecoveryCodes, code => Assert.DoesNotContain(code.Replace("-", ""), row.Hashes, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(10, row.Hashes.Split(';').Length);
    }

    private static byte[] Base32Decode(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }

    // ---- confirming

    [Fact]
    public async Task WithoutTheCodeThereIsNoConfirmationAndWithItTheKeysOpen()
    {
        // The vault runs on this clock, which does not move: every code is made for the vault's time, never for the real one.
        var (client, clock, _, _) = Host();
        var (_, _, access) = FreshAdministrator("confirm");
        var key = (await ReadAsync<SetupBody>(await PostAsync(client, "/trading-code/begin", access))).SharedKey;
        await PostAsync(client, "/trading-code/setup", access, new { Code = TestTotp.Code(key, clock.GetUtcNow()) });

        var keyBody = new { Label = "k", Environment = "Demo" };
        var noToken = await PostAsync(client, "/keys", access, keyBody);
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
        Assert.Contains("step_up_required", await noToken.Content.ReadAsStringAsync(Ct));

        var wrong = await PostAsync(client, "/trading-code/confirmation", access, new { Code = "123456" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        // The next step's code: the one of this step was taken by the setup, and a code works once.
        var next = TestTotp.Code(key, clock.GetUtcNow().AddSeconds(30));
        var confirmed = await ReadAsync<ConfirmationBody>(await PostAsync(client, "/trading-code/confirmation", access, new { Code = next }));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/keys") { Content = JsonContent.Create(keyBody) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Add(RequireTradingConfirmationFilter.Header, confirmed.Token);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task AnAdministratorWithoutATradingCodeCannotDoAnythingThatMattersAndIsToldWhy()
    {
        var (client, _, _, _) = Host();
        var (_, _, access) = FreshAdministrator("nocode");

        var response = await PostAsync(client, "/keys", access, new { Label = "k", Environment = "Demo" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("trading_code_not_set", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(client, "/trading-code/confirmation", access, new { Code = "123456" })).StatusCode);
    }

    [Fact]
    public async Task ACodeWorksOnceAndAnOlderStepIsNotASecondChance()
    {
        var (client, clock, _, _) = Host();
        var (_, _, access) = FreshAdministrator("once");
        var key = (await ReadAsync<SetupBody>(await PostAsync(client, "/trading-code/begin", access))).SharedKey;
        // The vault runs on a clock that does not move, so the codes are made for that clock and not for the real time. With the real time a slow
        // runner could be a boundary of 30 seconds ahead of the vault when the code was made, and the code of the step after the vault's window
        // was refused (once in a while). The setup takes the step of the vault's now.
        var firstStep = clock.GetUtcNow();
        var setup = await PostAsync(client, "/trading-code/setup", access, new { Code = TestTotp.Code(key, firstStep) });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);

        // The next step is inside the window too (the clock only moves forward), and later than the one taken.
        var later = TestTotp.Code(key, firstStep.AddSeconds(30));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "/trading-code/confirmation", access, new { Code = later })).StatusCode);

        var again = await PostAsync(client, "/trading-code/confirmation", access, new { Code = later });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("already used", DetailOf(await again.Content.ReadAsStringAsync(Ct)));

        var older = await PostAsync(client, "/trading-code/confirmation", access, new { Code = TestTotp.Code(key, firstStep) });
        Assert.Equal(HttpStatusCode.BadRequest, older.StatusCode);   // the step of now is older than the one taken
    }

    [Fact]
    public void TheCodeOfTheStepBeforeIsOnlyInsideTheWindowUntilTheClockMovesOnSoATestMustNotRelyOnIt()
    {
        // This is what made the test above fail once in a while on a slow runner: its first code was made for now - 30 s, one second
        // before a boundary, and by the time it arrived the boundary had passed. Fixed times, so that it is shown every time.
        var secret = Totp.NewSecret();
        var oneSecondBeforeABoundary = new DateTimeOffset(2026, 11, 5, 12, 0, 29, TimeSpan.Zero);
        var stepBefore = Totp.CodeAt(secret, Totp.StepOf(oneSecondBeforeABoundary) - 1);
        var stepOfNow = Totp.CodeAt(secret, Totp.StepOf(oneSecondBeforeABoundary));

        Assert.NotNull(Totp.Match(secret, stepBefore, oneSecondBeforeABoundary));
        Assert.Null(Totp.Match(secret, stepBefore, oneSecondBeforeABoundary.AddSeconds(2)));     // the clock crossed the boundary: out of the window
        Assert.NotNull(Totp.Match(secret, stepOfNow, oneSecondBeforeABoundary.AddSeconds(2)));   // the step of now stays inside whichever side it lands
    }

    [Fact]
    public async Task AnotherUsersConfirmationIsNotMine()
    {
        var (client, _, _, _) = Host();
        var mine = FreshAdministrator("mine");
        var key = (await ReadAsync<SetupBody>(await PostAsync(client, "/trading-code/begin", mine.AccessToken))).SharedKey;
        await PostAsync(client, "/trading-code/setup", mine.AccessToken, new { Code = TestTotp.Code(key) });
        var other = trader.Administrator("someone-else");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/keys") { Content = JsonContent.Create(new { Label = "k", Environment = "Demo" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mine.AccessToken);
        request.Headers.Add(RequireTradingConfirmationFilter.Header, other.Elevation);   // a real confirmation, of somebody else

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task AnOrdinaryUserHasNoTradingCodeToSetUp()
    {
        var (client, _, _, _) = Host();
        var ordinary = trader.OrdinaryUser();

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(client, "/trading-code/begin", ordinary.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/trading-code/begin", null, Ct)).StatusCode);
    }

    // ---- wrong codes

    [Fact]
    public async Task TheSecondWrongCodeSendsAMessageAndTheFifthLocksForAWhile()
    {
        var (client, clock, alerts, _) = Host();
        var (_, name, access) = FreshAdministrator("locked");
        var key = (await ReadAsync<SetupBody>(await PostAsync(client, "/trading-code/begin", access))).SharedKey;
        await PostAsync(client, "/trading-code/setup", access, new { Code = TestTotp.Code(key, clock.GetUtcNow()) });
        var before = alerts.Raised.Count;

        await PostAsync(client, "/trading-code/confirmation", access, new { Code = "000000" });
        Assert.Equal(before, alerts.Raised.Count);                                       // one wrong code can be a slip
        await PostAsync(client, "/trading-code/confirmation", access, new { Code = "000001" });
        Assert.Contains(alerts.Raised.Skip(before), alert => alert.Title.Contains(name) && alert.Title.Contains("Wrong"));

        for (var i = 0; i < 3; i++)
            await PostAsync(client, "/trading-code/confirmation", access, new { Code = "000002" });
        Assert.Contains(alerts.Raised, alert => alert.Title.Contains("locked") && alert.Title.Contains(name));

        // Locked: even the right code is refused, for fifteen minutes, and then it works again.
        var right = TestTotp.Code(key, clock.GetUtcNow().AddSeconds(30));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(client, "/trading-code/confirmation", access, new { Code = right })).StatusCode);
        var status = await ReadAsync<StatusBody>(await GetAsync(client, "/trading-code", access));
        Assert.NotNull(status.LockedUntil);

        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "/trading-code/confirmation", access, new { Code = TestTotp.Code(key, clock.GetUtcNow()) })).StatusCode);
    }

    [Fact]
    public async Task EveryRoundOfWrongCodesLocksForLongerAndNeverForGood()
    {
        var (client, clock, _, services) = Host();
        var (id, _, access) = FreshAdministrator("rounds");
        var key = (await ReadAsync<SetupBody>(await PostAsync(client, "/trading-code/begin", access))).SharedKey;
        await PostAsync(client, "/trading-code/setup", access, new { Code = TestTotp.Code(key, clock.GetUtcNow()) });
        var codes = services.GetRequiredService<TradingCodeRepository>();

        TimeSpan[] expected = [.. TradingCodePolicy.Locks, TradingCodePolicy.Locks[^1]];
        for (var round = 1; round <= expected.Length; round++)
        {
            for (var i = 0; i < TradingCodePolicy.Attempts; i++)
                await PostAsync(client, "/trading-code/confirmation", access, new { Code = "000000" });

            var row = (await codes.FindAsync(id, Ct))!;
            Assert.Equal(round, row.Rounds);
            Assert.Equal(clock.GetUtcNow().UtcDateTime + expected[round - 1], row.LockedUntil!.Value, TimeSpan.FromSeconds(1));   // the database keeps microseconds
            clock.Advance(expected[round - 1] + TimeSpan.FromSeconds(1));
        }

        Assert.Equal([15, 60, 360, 1440, 1440], expected.Select(e => (int)e.TotalMinutes));
    }

    [Fact]
    public async Task ARightCodeStartsTheCountOver()
    {
        var (client, clock, _, services) = Host();
        var (id, _, access) = FreshAdministrator("startover");
        var key = (await ReadAsync<SetupBody>(await PostAsync(client, "/trading-code/begin", access))).SharedKey;
        await PostAsync(client, "/trading-code/setup", access, new { Code = TestTotp.Code(key, clock.GetUtcNow()) });

        for (var i = 0; i < 4; i++)
            await PostAsync(client, "/trading-code/confirmation", access, new { Code = "000000" });
        await PostAsync(client, "/trading-code/confirmation", access, new { Code = TestTotp.Code(key, clock.GetUtcNow().AddSeconds(30)) });

        var row = (await services.GetRequiredService<TradingCodeRepository>().FindAsync(id, Ct))!;
        Assert.Equal((0, 0, null), (row.Failures, row.Rounds, row.LockedUntil));
    }

    // ---- the way back

    [Fact]
    public async Task ARecoveryCodeStartsTheSetupAgainOnceAndTheOldConfirmationsEnd()
    {
        var (client, _, alerts, _) = Host();
        var (_, name, access) = FreshAdministrator("recover");
        var key = (await ReadAsync<SetupBody>(await PostAsync(client, "/trading-code/begin", access))).SharedKey;
        var enrolled = await ReadAsync<EnrolledBody>(await PostAsync(client, "/trading-code/setup", access, new { Code = TestTotp.Code(key) }));

        var reset = await PostAsync(client, "/trading-code/reset", access, new { RecoveryCode = enrolled.RecoveryCodes[3].ToLowerInvariant() });   // case and the dash do not matter
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Contains(alerts.Raised, alert => alert.Title.Contains("reset") && alert.Title.Contains(name));

        var status = await ReadAsync<StatusBody>(await GetAsync(client, "/trading-code", access));
        Assert.False(status.Enrolled);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/keys") { Content = JsonContent.Create(new { Label = "k", Environment = "Demo" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Add(RequireTradingConfirmationFilter.Header, enrolled.ConfirmationToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request, Ct)).StatusCode);          // the confirmation from before the reset is gone

        // A new setup gives new keys, and the old recovery code is not good for the new code.
        var second = await ReadAsync<SetupBody>(await PostAsync(client, "/trading-code/begin", access));
        Assert.NotEqual(key, second.SharedKey);
    }

    [Fact]
    public async Task AWrongRecoveryCodeCountsLikeAWrongCodeAndResetsNothing()
    {
        var (client, _, _, _) = Host();
        var (_, _, access) = FreshAdministrator("recoverwrong");
        var key = (await ReadAsync<SetupBody>(await PostAsync(client, "/trading-code/begin", access))).SharedKey;
        await PostAsync(client, "/trading-code/setup", access, new { Code = TestTotp.Code(key) });

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await PostAsync(client, "/trading-code/reset", access, new { RecoveryCode = "AAAAA-AAAAA" })).StatusCode);

        Assert.Equal([.. Enumerable.Repeat(HttpStatusCode.BadRequest, 4), HttpStatusCode.Conflict], statuses);   // the fifth locked
        Assert.True((await ReadAsync<StatusBody>(await GetAsync(client, "/trading-code", access))).Enrolled);
    }

    [Fact]
    public async Task ARecoveryCodeWorksOnlyOnce()
    {
        var (client, _, _, _) = Host();
        var (_, _, access) = FreshAdministrator("recoveronce");
        var key = (await ReadAsync<SetupBody>(await PostAsync(client, "/trading-code/begin", access))).SharedKey;
        var enrolled = await ReadAsync<EnrolledBody>(await PostAsync(client, "/trading-code/setup", access, new { Code = TestTotp.Code(key) }));
        await PostAsync(client, "/trading-code/reset", access, new { RecoveryCode = enrolled.RecoveryCodes[0] });
        var again = await ReadAsync<SetupBody>(await PostAsync(client, "/trading-code/begin", access));
        await PostAsync(client, "/trading-code/setup", access, new { Code = TestTotp.Code(again.SharedKey) });

        // The code of the first setup is not one of the second's recovery codes.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/trading-code/reset", access, new { RecoveryCode = enrolled.RecoveryCodes[0] })).StatusCode);
    }

    [Fact]
    public async Task TheServersCommandForgetsTheCodeOfOneUserAndSaysSo()
    {
        var (client, _, alerts, services) = Host();
        var (id, _, access) = FreshAdministrator("console");
        var key = (await ReadAsync<SetupBody>(await PostAsync(client, "/trading-code/begin", access))).SharedKey;
        await PostAsync(client, "/trading-code/setup", access, new { Code = TestTotp.Code(key) });

        Assert.Equal(2, await TradingCodeCli.ResetAsync(services, "not-a-guid"));
        Assert.Equal(1, await TradingCodeCli.ResetAsync(services, Guid.NewGuid().ToString()));
        Assert.True((await ReadAsync<StatusBody>(await GetAsync(client, "/trading-code", access))).Enrolled);

        Assert.Equal(0, await TradingCodeCli.ResetAsync(services, id.ToString()));

        Assert.False((await ReadAsync<StatusBody>(await GetAsync(client, "/trading-code", access))).Enrolled);
        Assert.Contains(alerts.Raised, alert => alert.Body.Contains("server console"));
    }

    [Fact]
    public async Task WhatMattersIsInTheAuditLogAsTheSystemAndNeverHoldsACode()
    {
        var (client, _, _, _) = Host();
        var (_, name, access) = FreshAdministrator("audited-code");
        var key = (await ReadAsync<SetupBody>(await PostAsync(client, "/trading-code/begin", access))).SharedKey;
        var code = TestTotp.Code(key);
        await PostAsync(client, "/trading-code/setup", access, new { Code = code });

        await using var connection = new NpgsqlConnection(trader.ConnectionString);
        var rows = (await connection.QueryAsync<(string UserName, string Details)>(
            """SELECT "UserName", "Details" FROM config.audit_log WHERE "Summary" = @name AND "EntityType" = 'User'""", new { name })).ToList();

        var row = Assert.Single(rows);
        Assert.Equal("system", row.UserName);
        Assert.Contains("set up the trading code", row.Details);
        Assert.DoesNotContain(code, row.Details);
        Assert.DoesNotContain(key, row.Details);
    }
}

/// <summary>The code of an authenticator app, from the vault's own implementation, against the values of RFC 6238.</summary>
public class TotpTests
{
    private static readonly byte[] Rfc6238Secret = System.Text.Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]             // the RFC's 94287082 at 8 digits, last six
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void TheCodesAreTheOnesOfTheStandard(long seconds, string expected)
    {
        Assert.Equal(expected, VaultTotp.CodeAt(Rfc6238Secret, VaultTotp.StepOf(DateTimeOffset.FromUnixTimeSeconds(seconds))));
    }

    [Fact]
    public void ACodeOfTheStepBeforeOrAfterIsAcceptedAndTheStepIsReported()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var step = VaultTotp.StepOf(now);

        Assert.Equal(step - 1, VaultTotp.Match(Rfc6238Secret, VaultTotp.CodeAt(Rfc6238Secret, step - 1), now));
        Assert.Equal(step, VaultTotp.Match(Rfc6238Secret, VaultTotp.CodeAt(Rfc6238Secret, step), now));
        Assert.Equal(step + 1, VaultTotp.Match(Rfc6238Secret, VaultTotp.CodeAt(Rfc6238Secret, step + 1), now));
        Assert.Null(VaultTotp.Match(Rfc6238Secret, VaultTotp.CodeAt(Rfc6238Secret, step + 2), now));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("12345")]
    [InlineData("abcdef")]
    public void WhatIsNotSixDigitsMatchesNothing(string? typed)
    {
        Assert.Null(VaultTotp.Match(Rfc6238Secret, typed, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ADigitGroupedTheWayAppsShowItIsAccepted()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(59);
        var code = VaultTotp.CodeAt(Rfc6238Secret, VaultTotp.StepOf(now));

        Assert.NotNull(VaultTotp.Match(Rfc6238Secret, $"{code[..3]} {code[3..]}", now));
    }
}
