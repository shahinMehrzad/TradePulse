using System.Security.Claims;

namespace WebRazor.Api;

/// <summary>
/// Adds the confirmation the key vault gave this session (after the trading code) to every call to the vault, and only to those: it is the vault's
/// own token and means nothing to the API. The panel does not decide whether it is good; the vault does.
/// </summary>
public sealed class TraderConfirmationHandler(IHttpContextAccessor httpContextAccessor, SessionTokenStore store) : DelegatingHandler
{
    public const string Header = "X-Trading-Confirmation";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sessionId = httpContextAccessor.HttpContext?.User.FindFirstValue(ApiAuthHandler.SessionClaim);
        if (sessionId is not null && store.TryGetTraderConfirmation(sessionId, out var token))
            request.Headers.TryAddWithoutValidation(Header, token);

        return base.SendAsync(request, cancellationToken);
    }
}
