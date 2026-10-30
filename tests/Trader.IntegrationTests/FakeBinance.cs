using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace Trader.IntegrationTests;

/// <summary>
/// A stand-in for Binance's two hosts. It checks the signature of every request the way Binance documents it (RSASSA-PKCS1-v1_5 over
/// the query without the signature, base64, URL-encoded) against the public key that was registered for the API key, so the
/// signing of the program is tested by something that does not share its code.
/// </summary>
public sealed class FakeBinance : HttpMessageHandler
{
    private readonly Dictionary<string, RSA> _keys = [];
    private readonly Lock _lock = new();

    /// <summary>What <c>/sapi/v1/account/apiRestrictions</c> answers with, per API key. Missing: the Wallet API answers 404, like a host that has none.</summary>
    public Dictionary<string, string> Permissions { get; } = [];

    public List<string> Requests { get; } = [];

    public const string GoodPermissions = """{"ipRestrict":true,"enableReading":true,"enableWithdrawals":false,"enableInternalTransfer":false,"permitsUniversalTransfer":false,"enableFutures":true,"enableMargin":false,"enableSpotAndMarginTrading":false}""";

    public void Register(string apiKey, string publicKeyPem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        lock (_lock)
            _keys[apiKey] = rsa;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var apiKey = request.Headers.TryGetValues("X-MBX-APIKEY", out var values) ? values.First() : "";
        lock (_lock)
            Requests.Add($"{request.RequestUri!.AbsolutePath} key=…{(apiKey.Length > 4 ? apiKey[^4..] : apiKey)}");

        RSA? rsa;
        lock (_lock)
            _keys.TryGetValue(apiKey, out rsa);
        if (rsa is null)
            return Task.FromResult(Json(HttpStatusCode.Unauthorized, """{"code":-2008,"msg":"Invalid Api-Key ID."}"""));

        var query = request.RequestUri.Query.TrimStart('?');
        var separator = query.LastIndexOf("&signature=", StringComparison.Ordinal);
        if (separator < 0)
            return Task.FromResult(Json(HttpStatusCode.BadRequest, """{"code":-1102,"msg":"Mandatory parameter 'signature' was not sent."}"""));

        var payload = query[..separator];
        var signature = HttpUtility.UrlDecode(query[(separator + "&signature=".Length)..]);
        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            return Task.FromResult(Json(HttpStatusCode.BadRequest, """{"code":-1022,"msg":"Signature for this request is not valid."}"""));
        }

        if (!rsa.VerifyData(Encoding.ASCII.GetBytes(payload), signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            return Task.FromResult(Json(HttpStatusCode.BadRequest, """{"code":-1022,"msg":"Signature for this request is not valid."}"""));

        return Task.FromResult(request.RequestUri.AbsolutePath switch
        {
            "/fapi/v2/balance" => Json(HttpStatusCode.OK, """[{"asset":"USDT","balance":"0.00"}]"""),
            "/sapi/v1/account/apiRestrictions" when Permissions.TryGetValue(apiKey, out var body) => Json(HttpStatusCode.OK, body),
            _ => Json(HttpStatusCode.NotFound, """{"code":-1121,"msg":"Invalid endpoint."}""")
        });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
