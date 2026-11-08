using Application.Signals;
using Microsoft.Extensions.Time.Testing;

namespace Application.UnitTests;

/// <summary>Who may send signals: an HMAC over the time and the body, with a secret both sides have.</summary>
public class SignalAuthenticatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 8, 0, 2, TimeSpan.Zero);
    private const string Body = """{"signalId":"S1:BTCUSDT:4h:2026-11-07T04:00Z:long"}""";
    private const string Secret = "current-secret-of-the-sender";
    private const string Previous = "the-secret-before-it";

    private readonly FakeTimeProvider _time = new(Now);

    private SignalAuthenticator NewAuthenticator(params string[] secrets) =>
        new(new SignalIntakeOptions { Secrets = secrets.Length == 0 ? [Secret] : secrets }, _time);

    private static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    [Fact]
    public void ARequestSignedWithTheSecretIsAuthentic()
    {
        var stamp = Stamp(Now);

        Assert.True(NewAuthenticator().IsAuthentic(stamp, SignalAuthenticator.Sign(Secret, stamp, Body), Body));
    }

    [Fact]
    public void TheSignatureMayCarryItsAlgorithmAndAnyCaseOfHexadecimal()
    {
        var stamp = Stamp(Now);
        var signature = SignalAuthenticator.Sign(Secret, stamp, Body);

        Assert.True(NewAuthenticator().IsAuthentic(stamp, "sha256=" + signature, Body));
        Assert.True(NewAuthenticator().IsAuthentic(stamp, signature.ToUpperInvariant(), Body));
    }

    [Fact]
    public void TheSecretBeforeTheCurrentOneIsStillAcceptedWhileItIsBeingChanged()
    {
        var stamp = Stamp(Now);

        Assert.True(NewAuthenticator(Secret, Previous).IsAuthentic(stamp, SignalAuthenticator.Sign(Previous, stamp, Body), Body));
        Assert.True(NewAuthenticator(Secret, Previous).IsAuthentic(stamp, SignalAuthenticator.Sign(Secret, stamp, Body), Body));
    }

    [Fact]
    public void AnotherSecretIsRefused()
    {
        var stamp = Stamp(Now);

        Assert.False(NewAuthenticator().IsAuthentic(stamp, SignalAuthenticator.Sign("not-the-secret", stamp, Body), Body));
    }

    [Fact]
    public void ABodyThatWasChangedAfterSigningIsRefused()
    {
        var stamp = Stamp(Now);
        var signature = SignalAuthenticator.Sign(Secret, stamp, Body);

        Assert.False(NewAuthenticator().IsAuthentic(stamp, signature, Body.Replace("long", "short")));
    }

    [Fact]
    public void ATimeThatWasChangedAfterSigningIsRefused()
    {
        var signature = SignalAuthenticator.Sign(Secret, Stamp(Now), Body);

        Assert.False(NewAuthenticator().IsAuthentic(Stamp(Now.AddSeconds(1)), signature, Body));
    }

    [Fact]
    public void ARequestFiveMinutesFromOurClockIsTakenAndOneSecondMoreIsNot()
    {
        var authenticator = NewAuthenticator();

        foreach (var offset in new[] { TimeSpan.FromMinutes(-5), TimeSpan.FromMinutes(5) })
        {
            var stamp = Stamp(Now + offset);
            Assert.True(authenticator.IsAuthentic(stamp, SignalAuthenticator.Sign(Secret, stamp, Body), Body));
        }

        foreach (var offset in new[] { TimeSpan.FromMinutes(-5) - TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1) })
        {
            var stamp = Stamp(Now + offset);
            Assert.False(authenticator.IsAuthentic(stamp, SignalAuthenticator.Sign(Secret, stamp, Body), Body));
        }
    }

    [Fact]
    public void ARecordedRequestCannotBeSentAgainLater()
    {
        var stamp = Stamp(Now);
        var signature = SignalAuthenticator.Sign(Secret, stamp, Body);
        var authenticator = NewAuthenticator();
        Assert.True(authenticator.IsAuthentic(stamp, signature, Body));

        _time.Advance(TimeSpan.FromMinutes(6));

        Assert.False(authenticator.IsAuthentic(stamp, signature, Body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yesterday")]
    public void ATimeThatIsMissingOrNotATimeIsRefused(string? stamp) =>
        Assert.False(NewAuthenticator().IsAuthentic(stamp, SignalAuthenticator.Sign(Secret, stamp ?? "", Body), Body));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not hexadecimal")]
    [InlineData("abc")]
    [InlineData("zz")]
    public void ASignatureThatIsMissingOrNotHexadecimalIsRefused(string? signature) =>
        Assert.False(NewAuthenticator().IsAuthentic(Stamp(Now), signature, Body));

    [Fact]
    public void WithoutAnySecretNobodyIsAuthentic()
    {
        var stamp = Stamp(Now);

        Assert.False(new SignalAuthenticator(new SignalIntakeOptions(), _time).IsAuthentic(stamp, SignalAuthenticator.Sign("", stamp, Body), Body));
    }

    [Fact]
    public void AnEmptySecretInTheListNeverMakesARequestAuthentic()
    {
        var stamp = Stamp(Now);

        Assert.False(NewAuthenticator("", Secret).IsAuthentic(stamp, SignalAuthenticator.Sign("", stamp, Body), Body));
    }

    [Fact]
    public void TheSignatureIsTheHmacOfTheTimeAndTheBodyJoinedByADot()
    {
        // The contract in one line, for whoever writes a sender: HMAC-SHA256(secret, timestamp + "." + body), lower-case hexadecimal.
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("k"), System.Text.Encoding.UTF8.GetBytes("2026-11-07T08:00:02Z.{}")));

        Assert.Equal(expected, SignalAuthenticator.Sign("k", "2026-11-07T08:00:02Z", "{}"));
    }
}
