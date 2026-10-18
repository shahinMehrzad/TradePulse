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
                _sessions.TryRemove(id, out _);

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

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);
}
