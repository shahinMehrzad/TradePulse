using Application.Interfaces;
using Infrastructure.Configurations;
using Microsoft.Extensions.Options;

namespace Infrastructure.Identity;

/// <summary>The proof that the password was right, between the steps of a sign-in (see <see cref="IChallengeTokenService"/>).</summary>
public sealed class JwtChallengeTokenService(IOptions<JwtConfigurations> options, TimeProvider time) : IChallengeTokenService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly PurposeTokens _tokens = new(options.Value, time, "login-challenge", Lifetime);

    public ChallengeToken Create(Guid userId)
    {
        var (token, expiresAt) = _tokens.Create(userId);
        return new ChallengeToken(token, expiresAt);
    }

    public Guid? Validate(string token) => _tokens.Validate(token);
}
