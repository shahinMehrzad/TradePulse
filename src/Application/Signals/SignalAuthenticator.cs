using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Application.Signals;

/// <summary>
/// How the sender of signals proves who it is: <c>X-Signature</c> is the HMAC-SHA256 of <c>X-Timestamp + "." + body</c> with a secret both
/// sides have, in lower-case hexadecimal (a leading <c>sha256=</c> is allowed). <c>X-Timestamp</c> is the time of that request (UTC, ISO 8601)
/// and must be within <see cref="SignalIntakeOptions.MaxRequestSkew"/> of ours, so a recorded request cannot be sent again later; a request
/// sent again at once is harmless, the signal id makes it the same signal. A retry makes a new timestamp and a new signature.
/// <para>
/// Up to two secrets are accepted, the current and the previous, so the secret can be changed without a moment in which the sender is refused.
/// The secrets are never written anywhere, and every refusal looks the same from outside.
/// </para>
/// </summary>
public sealed class SignalAuthenticator(SignalIntakeOptions options, TimeProvider time)
{
    public bool IsAuthentic(string? timestamp, string? signature, string body)
    {
        if (string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(signature) || options.Secrets.Count == 0)
            return false;

        if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var sent))
            return false;

        if ((time.GetUtcNow() - sent).Duration() > options.MaxRequestSkew)
            return false;

        var given = Hex(signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase) ? signature[7..] : signature);
        if (given is null)
            return false;

        var message = Encoding.UTF8.GetBytes(timestamp + "." + body);
        var matched = false;

        // Every secret is tried, and the answer is the same however early one matches: the time does not tell which secret it was.
        foreach (var secret in options.Secrets.Where(s => !string.IsNullOrEmpty(s)))
        {
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message);
            matched |= CryptographicOperations.FixedTimeEquals(expected, given);
        }

        return matched;
    }

    private static byte[]? Hex(string text)
    {
        try
        {
            return Convert.FromHexString(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The signature a sender makes for a request, for the tests and for whoever writes a sender.</summary>
    public static string Sign(string secret, string timestamp, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + body)));
}
