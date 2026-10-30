using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace WebRazor.Api;

public enum LoginFailure { None, InvalidCredentials, NotAnAdmin, TooManyAttempts, ApiUnavailable, Refused }

/// <summary>Where the sign-in goes next after a step that worked.</summary>
public enum LoginNext { SignedIn, TwoFactor, Setup }

/// <summary>
/// A sign-in between its steps: the password was right and the API handed out a challenge token. The panel keeps it in an encrypted,
/// short-lived cookie (see <see cref="PendingLoginCookie"/>), never in the page.
/// </summary>
/// <param name="EmailCodeAvailable">At the code step: the user turned email codes on and the server can send them.</param>
public record PendingLogin(string SessionId, string ChallengeToken, bool MustChangePassword, bool NeedsTwoFactorSetup, string? ReturnUrl = null, bool EmailCodeAvailable = false);

/// <param name="Message">What the API said when it refused a step after the first (wrong code, weak password); safe to show.</param>
public record LoginOutcome(
    LoginFailure Failure,
    LoginNext Next = LoginNext.SignedIn,
    string? SessionId = null,
    AdminInfo? Admin = null,
    PendingLogin? Pending = null,
    string? Message = null,
    string[]? RecoveryCodes = null);

public record EnrollmentOutcome(LoginFailure Failure, TwoFactorEnrollment? Enrollment = null, string? Message = null);

/// <summary>
/// Signs in against WebApi, one step per call, and lets only an administrator into the panel. The role check is WebApi's own
/// (<c>GET /api/admin/me</c> answers 403 to anyone else), so the panel never decodes or trusts a token itself.
/// </summary>
public sealed class AdminLoginService(IHttpClientFactory clientFactory, SessionTokenStore store)
{
    public async Task<LoginOutcome> LoginAsync(string identifier, string password, CancellationToken cancellationToken)
    {
        var isEmail = identifier.Contains('@');
        // Doubles as the device id: refresh tokens are bound to it.
        var sessionId = Guid.NewGuid().ToString("N");

        return await StepAsync(sessionId, "api/auth/login", new
        {
            Email = isEmail ? identifier : string.Empty,
            Username = isEmail ? string.Empty : identifier,
            Password = password
        }, firstStep: true, cancellationToken);
    }

    /// <param name="method">Authenticator, Recovery or Email.</param>
    public Task<LoginOutcome> VerifyTwoFactorAsync(PendingLogin pending, string code, string method, CancellationToken cancellationToken) =>
        StepAsync(pending, "api/auth/login/two-factor", new { pending.ChallengeToken, Code = code, Method = method }, cancellationToken);

    /// <summary>Asks the API to mail a sign-in code. Failure.None when it was sent; otherwise the reason (too soon, no mail server, ...).</summary>
    public async Task<LoginOutcome> SendEmailCodeAsync(PendingLogin pending, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await PostAsync(pending.SessionId, "api/auth/login/two-factor/email", new { pending.ChallengeToken }, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new LoginOutcome(LoginFailure.TooManyAttempts);

            return response.IsSuccessStatusCode
                ? new LoginOutcome(LoginFailure.None)
                : new LoginOutcome(LoginFailure.Refused, Message: await ReadMessageAsync(response, cancellationToken));
        }
        catch (HttpRequestException)
        {
            return new LoginOutcome(LoginFailure.ApiUnavailable);
        }
    }

    public Task<LoginOutcome> ChangePasswordAsync(PendingLogin pending, string currentPassword, string newPassword, CancellationToken cancellationToken) =>
        StepAsync(pending, "api/auth/setup/password", new { pending.ChallengeToken, CurrentPassword = currentPassword, NewPassword = newPassword }, cancellationToken);

    public Task<LoginOutcome> ConfirmTwoFactorAsync(PendingLogin pending, string code, CancellationToken cancellationToken) =>
        StepAsync(pending, "api/auth/setup/two-factor/confirm", new { pending.ChallengeToken, Code = code }, cancellationToken);

    /// <summary>The key (and the address the QR code encodes) for the authenticator app.</summary>
    public async Task<EnrollmentOutcome> BeginTwoFactorAsync(PendingLogin pending, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await PostAsync(pending.SessionId, "api/auth/setup/two-factor/begin", new { pending.ChallengeToken }, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new EnrollmentOutcome(LoginFailure.TooManyAttempts);
            if (!response.IsSuccessStatusCode)
                return new EnrollmentOutcome(LoginFailure.Refused, Message: await ReadMessageAsync(response, cancellationToken));

            var enrollment = await response.Content.ReadFromJsonAsync<TwoFactorEnrollment>(cancellationToken)
                             ?? throw new InvalidOperationException("Empty enrollment response.");
            return new EnrollmentOutcome(LoginFailure.None, enrollment);
        }
        catch (HttpRequestException)
        {
            return new EnrollmentOutcome(LoginFailure.ApiUnavailable);
        }
    }

    private Task<LoginOutcome> StepAsync(PendingLogin pending, string url, object body, CancellationToken cancellationToken) =>
        StepAsync(pending.SessionId, url, body, firstStep: false, cancellationToken, pending.ReturnUrl);

    private async Task<LoginOutcome> StepAsync(string sessionId, string url, object body, bool firstStep, CancellationToken cancellationToken, string? returnUrl = null)
    {
        try
        {
            using var response = await PostAsync(sessionId, url, body, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new LoginOutcome(LoginFailure.TooManyAttempts);
            if (!response.IsSuccessStatusCode)
            {
                // The first step answers the same for every kind of failure: don't tell a stranger which accounts exist.
                return firstStep && response.StatusCode == HttpStatusCode.Unauthorized
                    ? new LoginOutcome(LoginFailure.InvalidCredentials)
                    : new LoginOutcome(LoginFailure.Refused, Message: await ReadMessageAsync(response, cancellationToken));
            }

            var login = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken)
                        ?? throw new InvalidOperationException("Empty login response.");
            return login.Stage switch
            {
                "Authenticated" => await FinishAsync(sessionId, login, cancellationToken),
                "TwoFactorRequired" => Pending(LoginNext.TwoFactor),
                "SetupRequired" => Pending(LoginNext.Setup),
                _ => throw new InvalidOperationException($"Unknown sign-in stage '{login.Stage}'.")
            };

            LoginOutcome Pending(LoginNext next) => new(LoginFailure.None, next, sessionId,
                Pending: new PendingLogin(sessionId, login.ChallengeToken!, login.MustChangePassword, login.NeedsTwoFactorSetup, returnUrl, login.EmailCodeAvailable));
        }
        catch (HttpRequestException)
        {
            return new LoginOutcome(LoginFailure.ApiUnavailable);
        }
    }

    /// <summary>The API signed the user in; the panel lets them in only if they are an administrator.</summary>
    private async Task<LoginOutcome> FinishAsync(string sessionId, LoginResponse login, CancellationToken cancellationToken)
    {
        var client = clientFactory.CreateClient(ApiAuthHandler.RawClientName);
        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "api/admin/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        using var meResponse = await client.SendAsync(meRequest, cancellationToken);
        if (meResponse.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            return new LoginOutcome(LoginFailure.NotAnAdmin);
        meResponse.EnsureSuccessStatusCode();

        var admin = await meResponse.Content.ReadFromJsonAsync<AdminInfo>(cancellationToken)
                    ?? throw new InvalidOperationException("Empty admin response.");

        store.Create(sessionId, new TokenSet(login.AccessToken!, login.RefreshToken!, login.ExpiresAt!.Value));
        return new LoginOutcome(LoginFailure.None, LoginNext.SignedIn, sessionId, admin, RecoveryCodes: login.RecoveryCodes);
    }

    private Task<HttpResponseMessage> PostAsync(string sessionId, string url, object body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add(ApiAuthHandler.DeviceHeader, sessionId);
        return clientFactory.CreateClient(ApiAuthHandler.RawClientName).SendAsync(request, cancellationToken);
    }

    /// <summary>The reason the API gave: <c>message</c> of its 401s, <c>detail</c> of its problem details.</summary>
    private static async Task<string?> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            foreach (var name in new[] { "message", "detail" })
                if (document.RootElement.TryGetProperty(name, out var text) && text.GetString() is { Length: > 0 } reason)
                    return reason;
        }
        catch (JsonException)
        {
            // Not JSON (a proxy's error page, say): the page falls back to a generic sentence.
        }

        return null;
    }
}
