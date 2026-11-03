using System.Collections.Concurrent;
using Application.Interfaces;
using Microsoft.Extensions.Options;

namespace WebApi.Authentication;

public sealed class AccessTokenGuardOptions
{
    public const string SectionName = "TokenRevocation";

    /// <summary>
    /// How long what is known about one user is reused before the database is asked again. It is the longest a disabled user, a removed role or a
    /// reset password still gets through with a token that is not expired yet (the API in which the change was made forgets it at once only when
    /// this is 0). 0 asks at every request, which the tests use.
    /// </summary>
    public int CacheSeconds { get; set; } = 10;
}

/// <summary>
/// A token is a signature and an expiry: nothing in it knows that the user was disabled a minute ago. So that a change takes hold at once and not
/// when the token runs out, the API asks, for every request with a valid token, whether the user is still the one the token was made for:
/// there is such a user, who is not disabled, who is not waiting to set a new password or two-factor up (an administrator reset them), and who
/// still has every role the token carries. What is known about a user is kept for <see cref="AccessTokenGuardOptions.CacheSeconds"/>, so a request
/// is not a database call each. The key vault cannot ask (it has no access to the accounts): it relies on the short life of the token instead.
/// </summary>
public sealed class AccessTokenGuard(IServiceScopeFactory scopes, TimeProvider time, IOptions<AccessTokenGuardOptions> options)
{
    private const int TrimAbove = 500;

    private readonly ConcurrentDictionary<Guid, Known> _known = new();

    /// <param name="Roles">The roles the user has now; null when the user may not use a token at all.</param>
    private sealed record Known(DateTimeOffset At, IReadOnlySet<string>? Roles);

    /// <returns>True when the token may still be used: the user is active and has every role it names.</returns>
    public async Task<bool> StillAcceptsAsync(Guid userId, IEnumerable<string> tokenRoles, CancellationToken cancellationToken)
    {
        var roles = await RolesNowAsync(userId, cancellationToken);
        return roles is not null && tokenRoles.All(roles.Contains);
    }

    private async Task<IReadOnlySet<string>?> RolesNowAsync(Guid userId, CancellationToken cancellationToken)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(0, options.Value.CacheSeconds));
        var now = time.GetUtcNow();
        if (ttl > TimeSpan.Zero && _known.TryGetValue(userId, out var known) && now - known.At < ttl)
            return known.Roles;

        await using var scope = scopes.CreateAsyncScope();
        var account = await scope.ServiceProvider.GetRequiredService<IIdentityService>().FindActiveAccountAsync(userId, cancellationToken);

        // The refresh of a session refuses an account that has to set up again; so does a token that was made before the reset.
        IReadOnlySet<string>? roles = account is null || account.NeedsSetup
            ? null
            : account.User.Roles.ToHashSet(StringComparer.Ordinal);

        if (ttl > TimeSpan.Zero)
        {
            if (_known.Count > TrimAbove)
                foreach (var (id, entry) in _known)
                    if (now - entry.At >= ttl)
                        _known.TryRemove(id, out _);

            _known[userId] = new Known(now, roles);
        }

        return roles;
    }
}
