using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Google.Protobuf;
using Infrastructure.Exchange.Ctrader;
using Infrastructure.Exchange.Ctrader.Protocol;

namespace CtraderCheck;

/// <summary>
/// Gets the access token: the owner of the account allows the application in the browser, cTrader sends the owner back to the address this tool
/// listens on, and the code that comes with it is exchanged for the tokens. Then it asks cTrader which accounts the token opens, to find the account id.
/// Where the secrets go: the access token (a read only one) to the secrets of the collector and the API, the refresh token to this tool's own.
/// </summary>
internal static class TokenCommand
{
    private static readonly TimeSpan WaitForTheOwner = TimeSpan.FromMinutes(5);

    public static async Task<int> RunAsync(CliOptions options, bool refresh)
    {
        var live = options.Get("env", "demo").Equals("live", StringComparison.OrdinalIgnoreCase);
        var scope = options.Get("scope", "accounts").Equals("trading", StringComparison.OrdinalIgnoreCase) ? CtraderScope.Trading : CtraderScope.Accounts;
        var clientId = Prompt.ClientId();
        var clientSecret = Prompt.ClientSecret();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        CtraderTokens tokens;
        if (refresh)
        {
            var old = Secrets.Get(Secrets.ThisTool, "CtraderCheck:RefreshToken")
                ?? throw new InvalidOperationException("There is no refresh token yet: run `token` first.");
            tokens = await CtraderOAuth.RefreshAsync(http, clientId, clientSecret, old, CancellationToken.None);
            scope = Enum.Parse<CtraderScope>(Secrets.Get(Secrets.ThisTool, "CtraderCheck:Scope") ?? nameof(CtraderScope.Accounts));
        }
        else
        {
            tokens = await GetFromTheOwnerAsync(http, clientId, clientSecret, options.Get("redirect", "http://localhost:5000/callback"), scope);
        }

        // The refresh token first: the one that was used is already dead, and this is the only copy of the new one.
        Secrets.Set(Secrets.ThisTool, new Dictionary<string, string>
        {
            ["CtraderCheck:RefreshToken"] = tokens.RefreshToken,
            ["CtraderCheck:Scope"] = scope.ToString(),
        });
        // cTrader does not say again when the token ends, so it is written down here: the collector warns a day before.
        var expiresAt = DateTimeOffset.UtcNow + tokens.ExpiresIn;
        Console.WriteLine($"Got tokens ({tokens}); the access token runs out at {expiresAt:yyyy-MM-dd HH:mm} UTC. The refresh token is kept in this tool's own secrets.");

        var accounts = await ListAccountsAsync(live, clientId, clientSecret, tokens.AccessToken);
        Console.WriteLine($"The token opens {accounts.CtidTraderAccount.Count} account(s) ({accounts.PermissionScope}):");
        foreach (var account in accounts.CtidTraderAccount)
            Console.WriteLine($"  id {account.CtidTraderAccountId}  {(account.IsLive ? "LIVE" : "demo")}  login {account.TraderLogin}  {account.BrokerTitleShort}");

        // A refresh keeps the account that was chosen before.
        var chosen = options.Values.GetValueOrDefault("account")
            ?? (refresh ? Secrets.Get(Secrets.Collector, "CtraderExchangeConfigurations:AccountId") : null);
        var wanted = accounts.CtidTraderAccount.Where(a => a.IsLive == live && (chosen is null || a.CtidTraderAccountId.ToString() == chosen)).ToList();

        if (scope == CtraderScope.Trading)
        {
            Console.WriteLine("This token can TRADE, so it is not written to the secrets of the collector or the API (they must never hold one). It is for the trading service.");
            PrintIfAsked(options, tokens, expiresAt);
            return 0;
        }

        if (wanted.Count != 1)
        {
            Console.WriteLine(wanted.Count == 0
                ? $"No {(live ? "live" : "demo")} account to use. Nothing was saved for the collector and the API."
                : "More than one account fits: run again with --account <id>. Nothing was saved for the collector and the API.");
            PrintIfAsked(options, tokens, expiresAt);
            return wanted.Count == 0 ? 1 : 0;
        }

        var values = new Dictionary<string, string>
        {
            ["CtraderExchangeConfigurations:Environment"] = live ? "Live" : "Demo",
            ["CtraderExchangeConfigurations:ClientId"] = clientId,
            ["CtraderExchangeConfigurations:ClientSecret"] = clientSecret,
            ["CtraderExchangeConfigurations:AccessToken"] = tokens.AccessToken,
            ["CtraderExchangeConfigurations:AccountId"] = wanted[0].CtidTraderAccountId.ToString(),
            ["CtraderExchangeConfigurations:AccessTokenExpiresAt"] = expiresAt.ToString("O"),
        };
        Secrets.Set(Secrets.Collector, values);
        Secrets.Set(Secrets.Api, values);
        Console.WriteLine($"Saved to the secrets of the collector and the API: account {wanted[0].CtidTraderAccountId}, valid for about {tokens.ExpiresIn.TotalDays:F0} days. Run `refresh` before then.");
        PrintIfAsked(options, tokens, expiresAt);
        return 0;
    }

    private static void PrintIfAsked(CliOptions options, CtraderTokens tokens, DateTimeOffset expiresAt)
    {
        if (!options.Has("print"))
            return;

        Console.WriteLine("--print was given, so here it is (for deploy/.env; do not paste it anywhere else, and do not give it to anybody or anything):");
        Console.WriteLine($"CTRADER_ACCESS_TOKEN={tokens.AccessToken}");
        Console.WriteLine($"CTRADER_ACCESS_TOKEN_EXPIRES_AT={expiresAt:O}");
    }

    private static async Task<CtraderTokens> GetFromTheOwnerAsync(HttpClient http, string clientId, string clientSecret, string redirect, CtraderScope scope)
    {
        var redirectUri = new Uri(redirect);
        if (redirectUri.Host is not ("localhost" or "127.0.0.1"))
            throw new ArgumentException("The redirect address must be on this computer (localhost).");

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://{redirectUri.Host}:{redirectUri.Port}/");
        listener.Start();

        Console.WriteLine($"Open this address in a browser where you are signed in to cTrader, and allow the application ({scope}):");
        Console.WriteLine();
        Console.WriteLine(CtraderOAuth.BuildAuthorizationUrl(clientId, redirect, scope));
        Console.WriteLine();
        Console.WriteLine($"Waiting for cTrader to send you back to {redirect} ...");

        using var timeout = new CancellationTokenSource(WaitForTheOwner);
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("Nobody came back within 5 minutes.");
            }

            // Only the redirect address itself counts: a browser also asks for /favicon.ico.
            if (context.Request.Url is not { } url || !url.AbsolutePath.Equals(redirectUri.AbsolutePath, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                continue;
            }

            var found = CtraderOAuth.TryReadCallback(url, out var code, out var problem);
            await Reply(context, found ? "Done. You can close this window and go back to the terminal." : problem ?? "Something went wrong.");
            if (!found)
                throw new InvalidOperationException(problem ?? "No code.");

            return await CtraderOAuth.ExchangeCodeAsync(http, clientId, clientSecret, code, redirect, CancellationToken.None);
        }
    }

    private static async Task Reply(HttpListenerContext context, string text)
    {
        var body = System.Text.Encoding.UTF8.GetBytes($"<html><body><p>{WebUtility.HtmlEncode(text)}</p></body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }

    /// <summary>Signs the application in (no account yet) and asks which accounts the token opens.</summary>
    private static async Task<ProtoOAGetAccountListByAccessTokenRes> ListAccountsAsync(bool live, string clientId, string clientSecret, string accessToken)
    {
        var host = live ? "live.ctraderapi.com" : "demo.ctraderapi.com";
        using var client = new TcpClient();
        await client.ConnectAsync(host, 5035);
        await using var tls = new SslStream(client.GetStream());
        await tls.AuthenticateAsClientAsync(host);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Exchange(tls, ProtoOAPayloadType.ProtoOaApplicationAuthReq, new ProtoOAApplicationAuthReq { ClientId = clientId, ClientSecret = clientSecret }, timeout.Token);
        var answer = await Exchange(tls, ProtoOAPayloadType.ProtoOaGetAccountsByAccessTokenReq, new ProtoOAGetAccountListByAccessTokenReq { AccessToken = accessToken }, timeout.Token);
        return ProtoOAGetAccountListByAccessTokenRes.Parser.ParseFrom(answer.Payload);
    }

    private static async Task<ProtoMessage> Exchange(Stream stream, ProtoOAPayloadType type, IMessage request, CancellationToken cancellationToken)
    {
        await CtraderFrameCodec.WriteAsync(stream, new ProtoMessage { PayloadType = (uint)type, Payload = request.ToByteString(), ClientMsgId = "1" }, cancellationToken);

        while (await CtraderFrameCodec.ReadAsync(stream, cancellationToken) is { } message)
        {
            if (message.PayloadType == (uint)ProtoOAPayloadType.ProtoOaErrorRes)
            {
                var error = ProtoOAErrorRes.Parser.ParseFrom(message.Payload);
                throw new CtraderException($"cTrader refused the request ({error.ErrorCode}): {error.Description}", error.ErrorCode);
            }

            if (message.ClientMsgId == "1")
                return message;
        }

        throw new CtraderException("cTrader closed the connection.");
    }
}
