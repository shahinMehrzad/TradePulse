using Application.Interfaces;
using Infrastructure.Configurations;
using Microsoft.Extensions.Options;

namespace Infrastructure.Identity;

/// <summary>The proof that a user entered a second-factor code a moment ago (see <see cref="IElevationTokenService"/>).</summary>
public sealed class JwtElevationTokenService(IOptions<JwtConfigurations> options, TimeProvider time) : IElevationTokenService
{
    /// <summary>Long enough to do a few sensitive things in a row, short enough that a stolen session cannot wait for them.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly PurposeTokens _tokens = new(options.Value, time, "elevation", Lifetime);

    public ElevationToken Create(Guid userId)
    {
        var (token, expiresAt) = _tokens.Create(userId);
        return new ElevationToken(token, expiresAt);
    }

    public Guid? Validate(string? token) => _tokens.Validate(token);
}
