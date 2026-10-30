using System.Net;
using System.Net.Http.Json;
using Dapper;
using Npgsql;
using Trader.Vault;

namespace Trader.IntegrationTests;

/// <summary>
/// The vault from the outside: who may do what to which key, what Binance has to say before a key is accepted, and that the private key
/// never leaves. Each test uses its own users, so they do not see each other's keys.
/// </summary>
public class ExchangeKeyTests(TraderFixture trader) : IClassFixture<TraderFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record KeyView(Guid Id, string Label, string Status, string PublicKeyPem, string? ApiKeyHint, string[] Problems, DateTime? VerifiedAt);

    private static string NewApiKey() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>(Ct))!;

    private async Task<KeyView> CreateAsync(TraderFixture.Caller caller, string label = "main")
    {
        var response = await trader.SendAsync(HttpMethod.Post, "/keys", caller, new { Label = label });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<KeyView>(response);
    }

    /// <summary>A key that Binance knows under an API key, with the permissions it should have unless a test says otherwise.</summary>
    private async Task<(KeyView Key, string ApiKey)> RegisteredAsync(TraderFixture.Caller caller, string permissions = FakeBinance.GoodPermissions)
    {
        var key = await CreateAsync(caller);
        var apiKey = NewApiKey();
        trader.Binance.Register(apiKey, key.PublicKeyPem);
        trader.Binance.Permissions[apiKey] = permissions;
        return (key, apiKey);
    }

    private Task<HttpResponseMessage> SetApiKeyAsync(TraderFixture.Caller caller, Guid id, string apiKey) =>
        trader.SendAsync(HttpMethod.Put, $"/keys/{id}/api-key", caller, new { ApiKey = apiKey });

    // ---- who may

    [Fact]
    public async Task NobodyWithoutATokenGetsAnything()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await trader.SendAsync(HttpMethod.Get, "/keys", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await trader.SendAsync(HttpMethod.Post, "/keys", null, new { Label = "x" })).StatusCode);
    }

    [Fact]
    public async Task OnlyAdministratorsMayUseTheVault()
    {
        var ordinary = trader.OrdinaryUser();

        Assert.Equal(HttpStatusCode.Forbidden, (await trader.SendAsync(HttpMethod.Get, "/keys", ordinary)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.SendAsync(HttpMethod.Post, "/keys", ordinary, new { Label = "x" })).StatusCode);
    }

    [Fact]
    public async Task ChangingAKeyNeedsAFreshConfirmationOfTheSameUser()
    {
        var admin = trader.Administrator();
        var other = trader.Administrator("someone");

        var none = await trader.SendAsync(HttpMethod.Post, "/keys", admin, new { Label = "x" }, elevated: false);
        var othersConfirmation = await trader.SendAsync(HttpMethod.Post, "/keys", admin, new { Label = "x" }, elevation: other.Elevation);
        var garbage = await trader.SendAsync(HttpMethod.Post, "/keys", admin, new { Label = "x" }, elevation: "not-a-token");

        foreach (var response in new[] { none, othersConfirmation, garbage })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("step_up_required", await response.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task ReadingTheListNeedsNoConfirmation()
    {
        Assert.Equal(HttpStatusCode.OK, (await trader.SendAsync(HttpMethod.Get, "/keys", trader.Administrator(), elevated: false)).StatusCode);
    }

    [Fact]
    public async Task EveryActionOnAKeyOfSomebodyElseIsNotFound()
    {
        var owner = trader.Administrator("owner");
        var intruder = trader.Administrator("intruder");
        var (key, apiKey) = await RegisteredAsync(owner);

        Assert.Equal(HttpStatusCode.NotFound, (await SetApiKeyAsync(intruder, key.Id, apiKey)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await trader.SendAsync(HttpMethod.Post, $"/keys/{key.Id}/verify", intruder)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await trader.SendAsync(HttpMethod.Delete, $"/keys/{key.Id}", intruder)).StatusCode);
        Assert.Empty(await ReadAsync<KeyView[]>(await trader.SendAsync(HttpMethod.Get, "/keys", intruder)));
        Assert.Single(await ReadAsync<KeyView[]>(await trader.SendAsync(HttpMethod.Get, "/keys", owner))); // still there
    }

    // ---- the private key never leaves

    [Fact]
    public async Task ANewKeyShowsOnlyThePublicKeyAndTheDatabaseHoldsTheEncryptedPrivateOne()
    {
        var admin = trader.Administrator();

        var response = await trader.SendAsync(HttpMethod.Post, "/keys", admin, new { Label = "main" });
        var body = await response.Content.ReadAsStringAsync(Ct);
        var key = System.Text.Json.JsonSerializer.Deserialize<KeyView>(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", key.PublicKeyPem);
        Assert.Equal("PendingApiKey", key.Status);
        Assert.DoesNotContain("PRIVATE", body);

        await using var connection = new NpgsqlConnection(trader.ConnectionString);
        var stored = await connection.QuerySingleAsync<byte[]>("SELECT private_key_encrypted FROM vault.exchange_keys WHERE id = @id", new { id = key.Id });
        Assert.DoesNotContain("PRIVATE", System.Text.Encoding.ASCII.GetString(stored));
        Assert.True(stored.Length > 2000); // a 4096-bit RSA key in PKCS#8, plus the nonce and the tag

        // It decrypts for this key and this owner and for nobody else.
        var crypto = trader.Service<VaultCrypto>();
        Assert.NotEmpty(crypto.Decrypt(stored, key.Id, admin.Id));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => crypto.Decrypt(stored, key.Id, Guid.NewGuid()));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => crypto.Decrypt(stored, Guid.NewGuid(), admin.Id));
    }

    [Fact]
    public async Task TheListNeverContainsAPrivateKeyOrTheWholeApiKey()
    {
        var admin = trader.Administrator();
        var (key, apiKey) = await RegisteredAsync(admin);
        await SetApiKeyAsync(admin, key.Id, apiKey);

        var body = await (await trader.SendAsync(HttpMethod.Get, "/keys", admin)).Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("PRIVATE", body);
        Assert.DoesNotContain(apiKey, body);
        Assert.Contains("…" + apiKey[^4..], body);
    }

    // ---- what Binance has to say

    [Fact]
    public async Task AKeyThatBinanceAcceptsBecomesActive()
    {
        var admin = trader.Administrator();
        var (key, apiKey) = await RegisteredAsync(admin);

        var response = await SetApiKeyAsync(admin, key.Id, apiKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var active = await ReadAsync<KeyView>(response);
        Assert.Equal("Active", active.Status);
        Assert.Empty(active.Problems);
        Assert.NotNull(active.VerifiedAt);
        // Both calls were made with the key, and the fake checked the signature of each against the registered public key.
        Assert.Contains(trader.Binance.Requests, r => r.StartsWith("/fapi/v2/balance") && r.EndsWith(apiKey[^4..]));
        Assert.Contains(trader.Binance.Requests, r => r.StartsWith("/sapi/v1/account/apiRestrictions") && r.EndsWith(apiKey[^4..]));
    }

    [Theory]
    [InlineData("""{"ipRestrict":true,"enableWithdrawals":true,"enableInternalTransfer":false,"permitsUniversalTransfer":false,"enableFutures":true}""", "Withdrawals")]
    [InlineData("""{"ipRestrict":true,"enableWithdrawals":false,"enableInternalTransfer":true,"permitsUniversalTransfer":false,"enableFutures":true}""", "Transfers")]
    [InlineData("""{"ipRestrict":true,"enableWithdrawals":false,"enableInternalTransfer":false,"permitsUniversalTransfer":true,"enableFutures":true}""", "Transfers")]
    [InlineData("""{"ipRestrict":false,"enableWithdrawals":false,"enableInternalTransfer":false,"permitsUniversalTransfer":false,"enableFutures":true}""", "trusted IPs")]
    [InlineData("""{"ipRestrict":true,"enableWithdrawals":false,"enableInternalTransfer":false,"permitsUniversalTransfer":false,"enableFutures":false}""", "Futures trading is not enabled")]
    [InlineData("""{"ipRestrict":true,"enableInternalTransfer":false,"permitsUniversalTransfer":false,"enableFutures":true}""", "Withdrawals")] // withdrawals not mentioned: not proven off
    public async Task AKeyWithTooMuchPowerOrTooLittleProtectionIsRejectedAndSaysWhy(string permissions, string mentions)
    {
        var admin = trader.Administrator();
        var (key, apiKey) = await RegisteredAsync(admin, permissions);

        var rejected = await ReadAsync<KeyView>(await SetApiKeyAsync(admin, key.Id, apiKey));

        Assert.Equal("Rejected", rejected.Status);
        Assert.Contains(rejected.Problems, p => p.Contains(mentions));
    }

    [Fact]
    public async Task IfBinanceCannotConfirmThePermissionsTheKeyIsNotAccepted()
    {
        var admin = trader.Administrator();
        var key = await CreateAsync(admin);
        var apiKey = NewApiKey();
        trader.Binance.Register(apiKey, key.PublicKeyPem); // signs fine, but the Wallet API has no answer

        var result = await ReadAsync<KeyView>(await SetApiKeyAsync(admin, key.Id, apiKey));

        Assert.Equal("Rejected", result.Status);
        Assert.Contains(result.Problems, p => p.Contains("Could not confirm what this key may do"));
    }

    [Fact]
    public async Task APublicKeyThatIsNotTheOneBinanceHasIsAnInvalidSignature()
    {
        var admin = trader.Administrator();
        var mine = await CreateAsync(admin, "mine");
        var other = await CreateAsync(admin, "other");
        var apiKey = NewApiKey();
        trader.Binance.Register(apiKey, other.PublicKeyPem); // Binance has the other key's public half
        trader.Binance.Permissions[apiKey] = FakeBinance.GoodPermissions;

        var result = await ReadAsync<KeyView>(await SetApiKeyAsync(admin, mine.Id, apiKey));

        Assert.Equal("Rejected", result.Status);
        Assert.Contains(result.Problems, p => p.Contains("did not accept the signature"));
    }

    [Fact]
    public async Task AnApiKeyBinanceDoesNotKnowIsRejected()
    {
        var admin = trader.Administrator();
        var key = await CreateAsync(admin);

        var result = await ReadAsync<KeyView>(await SetApiKeyAsync(admin, key.Id, NewApiKey()));

        Assert.Equal("Rejected", result.Status);
        Assert.Contains(result.Problems, p => p.Contains("does not know this API key"));
    }

    [Fact]
    public async Task AfterFixingTheRestrictionsAtBinanceAVerifyAcceptsTheKey()
    {
        var admin = trader.Administrator();
        var (key, apiKey) = await RegisteredAsync(admin, """{"ipRestrict":false,"enableWithdrawals":false,"enableInternalTransfer":false,"permitsUniversalTransfer":false,"enableFutures":true}""");
        Assert.Equal("Rejected", (await ReadAsync<KeyView>(await SetApiKeyAsync(admin, key.Id, apiKey))).Status);

        trader.Binance.Permissions[apiKey] = FakeBinance.GoodPermissions; // the user fixed it at Binance
        var verified = await ReadAsync<KeyView>(await trader.SendAsync(HttpMethod.Post, $"/keys/{key.Id}/verify", admin));

        Assert.Equal("Active", verified.Status);
        Assert.Empty(verified.Problems);
    }

    [Fact]
    public async Task VerifyingNeedsTheApiKeyFirst()
    {
        var admin = trader.Administrator();
        var key = await CreateAsync(admin);

        Assert.Equal(HttpStatusCode.Conflict, (await trader.SendAsync(HttpMethod.Post, $"/keys/{key.Id}/verify", admin)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("-----BEGIN PUBLIC KEY-----")]
    [InlineData("has spaces in it 0123456789012345")]
    public async Task SomethingThatIsNotAnApiKeyIsRefused(string apiKey)
    {
        var admin = trader.Administrator();
        var key = await CreateAsync(admin);

        Assert.Equal(HttpStatusCode.BadRequest, (await SetApiKeyAsync(admin, key.Id, apiKey)).StatusCode);
    }

    // ---- housekeeping

    [Fact]
    public async Task DeletingAKeyRemovesItAndItsPrivateKey()
    {
        var admin = trader.Administrator();
        var key = await CreateAsync(admin);

        Assert.Equal(HttpStatusCode.NoContent, (await trader.SendAsync(HttpMethod.Delete, $"/keys/{key.Id}", admin)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await trader.SendAsync(HttpMethod.Delete, $"/keys/{key.Id}", admin)).StatusCode);
        await using var connection = new NpgsqlConnection(trader.ConnectionString);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM vault.exchange_keys WHERE id = @id", new { id = key.Id }));
    }

    [Fact]
    public async Task AUserCannotHoldMoreThanFiveKeys()
    {
        var admin = trader.Administrator();
        for (var i = 0; i < ExchangeKeyRepository.MaxKeysPerUser; i++)
            await CreateAsync(admin, $"key {i}");

        var sixth = await trader.SendAsync(HttpMethod.Post, "/keys", admin, new { Label = "one too many" });

        Assert.Equal(HttpStatusCode.Conflict, sixth.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ALabelIsRequired(string label)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await trader.SendAsync(HttpMethod.Post, "/keys", trader.Administrator(), new { Label = label })).StatusCode);
    }

    [Fact]
    public async Task TheAuditLogRecordsWhatHappenedButNeverAKey()
    {
        var admin = trader.Administrator("audited");
        var (key, apiKey) = await RegisteredAsync(admin);
        await SetApiKeyAsync(admin, key.Id, apiKey);
        await trader.SendAsync(HttpMethod.Delete, $"/keys/{key.Id}", admin);

        await using var connection = new NpgsqlConnection(trader.ConnectionString);
        var entries = (await connection.QueryAsync<(string Action, string Summary, string Details)>(
            """SELECT "Action", "Summary", "Details" FROM config.audit_log WHERE "EntityType" = 'ExchangeKey' AND "UserName" = 'audited' ORDER BY "Id" """)).ToList();

        Assert.Equal(["Added", "Updated", "Removed"], entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.Equal("main", e.Summary));
        Assert.All(entries, e =>
        {
            Assert.DoesNotContain(apiKey, e.Details);
            Assert.DoesNotContain("BEGIN", e.Details);
            Assert.DoesNotContain(key.PublicKeyPem[27..60], e.Details);
        });
    }

    [Fact]
    public async Task WithoutAUsableMasterKeyTheProgramDoesNotStart()
    {
        using var host = trader.WithWebHostBuilder(builder => builder.UseSetting("Vault:MasterKey", "too-short"));

        Assert.ThrowsAny<Exception>(() => host.CreateClient());
        await Task.CompletedTask;
    }
}
