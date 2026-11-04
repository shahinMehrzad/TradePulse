using System.Collections.Concurrent;

namespace WebRazor.Api;

/// <summary>
/// Holds each panel session's API tokens on the server. The browser only gets a cookie with an opaque session id,
/// so neither the access nor the refresh token ever reaches it. Sessions live in memory: a restart signs the admin out.
/// </summary>
public sealed class SessionTokenStore(TimeProvider time)
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    private readonly ConcurrentDictionary<string, (TokenSet Tokens, DateTimeOffset Created)> _sessions = new();

    /// <summary>The session id is chosen by the caller because it also serves as the device id the refresh token is bound to.</summary>
    public void Create(string sessionId, TokenSet tokens)
    {
        var now = time.GetUtcNow();
        foreach (var (id, session) in _sessions)
            if (now - session.Created > SessionLifetime)
            {
                _sessions.TryRemove(id, out _);
                _elevations.TryRemove(id, out _);
            }

        _sessions[sessionId] = (tokens, now);
    }

    public bool TryGet(string sessionId, out TokenSet tokens)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && time.GetUtcNow() - session.Created <= SessionLifetime)
        {
            tokens = session.Tokens;
            return true;
        }

        tokens = null!;
        return false;
    }

    public void Update(string sessionId, TokenSet tokens)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            _sessions[sessionId] = (tokens, session.Created);
    }

    public void Remove(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        _elevations.TryRemove(sessionId, out _);
        _traderConfirmations.TryRemove(sessionId, out _);
    }

    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> _traderConfirmations = new();

    /// <summary>Keeps the confirmation the key vault gave after the trading code (it is the vault's own, see <see cref="TraderConfirmationHandler"/>).</summary>
    public void SetTraderConfirmation(string sessionId, string token, DateTime expiresAt)
    {
        if (_sessions.ContainsKey(sessionId))
            _traderConfirmations[sessionId] = (token, new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)));
    }

    public void ClearTraderConfirmation(string sessionId) => _traderConfirmations.TryRemove(sessionId, out _);

    /// <returns>True while the session holds a confirmation of the vault that has not run out.</returns>
    public bool TryGetTraderConfirmation(string sessionId, out string token)
    {
        if (_traderConfirmations.TryGetValue(sessionId, out var held) && held.ExpiresAt - time.GetUtcNow() > TimeSpan.FromSeconds(5))
        {
            token = held.Token;
            return true;
        }

        token = string.Empty;
        return false;
    }

    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> _elevations = new();

    /// <summary>Keeps the proof of a fresh second-factor code (see the StepUp page); it is sent to the API with every call until it expires.</summary>
    public void SetElevation(string sessionId, string token, DateTime expiresAt)
    {
        if (_sessions.ContainsKey(sessionId))
            _elevations[sessionId] = (token, new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)));
    }

    /// <returns>True while the session holds a confirmation that has not run out.</returns>
    public bool TryGetElevation(string sessionId, out string token)
    {
        if (_elevations.TryGetValue(sessionId, out var elevation) && elevation.ExpiresAt - time.GetUtcNow() > TimeSpan.FromSeconds(5))
        {
            token = elevation.Token;
            return true;
        }

        token = string.Empty;
        return false;
    }
}
