using Application.Services;

namespace Application.UnitTests;

public class RefreshTokenHasherTests
{
    [Fact]
    public void HashingIsDeterministic() =>
        Assert.Equal(RefreshTokenHasher.Hash("token"), RefreshTokenHasher.Hash("token"));

    [Fact]
    public void DifferentTokensGiveDifferentHashes() =>
        Assert.NotEqual(RefreshTokenHasher.Hash("token-a"), RefreshTokenHasher.Hash("token-b"));

    [Fact]
    public void TheHashIsNotTheTokenAndFitsTheStoredForm()
    {
        var raw = Convert.ToBase64String(new byte[32]);

        var hash = RefreshTokenHasher.Hash(raw);

        Assert.NotEqual(raw, hash);
        Assert.Equal(44, hash.Length); // base64 of 32 bytes, same width as the raw token the column already holds
    }
}
