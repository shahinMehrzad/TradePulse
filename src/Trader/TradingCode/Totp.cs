using System.Security.Cryptography;
using System.Text;

namespace Trader.TradingCode;

/// <summary>
/// The six-digit code of an authenticator app (RFC 6238: HMAC-SHA1, 30-second steps), made here and not by Identity: the secret behind it is
/// the vault's alone. Nothing in the API, which holds the secrets of the sign-in codes, can make these.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    private const int Digits = 6;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long StepOf(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    public static string CodeAt(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        var value = step;
        for (var i = 7; i >= 0; i--, value >>= 8)
            counter[i] = (byte)(value & 0xFF);

        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % (int)Math.Pow(10, Digits)).ToString($"D{Digits}");
    }

    /// <summary>
    /// The step whose code is <paramref name="typed"/>, looking one step back and one forward (a clock a few seconds off, a code typed slowly),
    /// or null. All three are compared, so how long it takes does not say which one matched.
    /// </summary>
    public static long? Match(byte[] secret, string? typed, DateTimeOffset now)
    {
        var digits = new string((typed ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length != Digits)
            return null;

        var typedBytes = Encoding.ASCII.GetBytes(digits);
        long? found = null;
        for (var step = StepOf(now) - 1; step <= StepOf(now) + 1; step++)
            if (CryptographicOperations.FixedTimeEquals(typedBytes, Encoding.ASCII.GetBytes(CodeAt(secret, step))))
                found = step;

        return found;
    }

    /// <summary>The secret as the app wants to read it: base32, no padding.</summary>
    public static string ToBase32(byte[] data)
    {
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
            result.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return result.ToString();
    }

    /// <summary>What the QR code holds. The name is not the one of the sign-in entry on purpose: two entries in the app that look alike are mixed up.</summary>
    public static string Uri(string userName, byte[] secret)
    {
        const string issuer = "TradePulse trading";
        return $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(userName)}" +
               $"?secret={ToBase32(secret)}&issuer={System.Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}";
    }
}
