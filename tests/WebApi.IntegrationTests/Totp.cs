using System.Security.Cryptography;

namespace WebApi.IntegrationTests;

/// <summary>The code an authenticator app shows (RFC 6238: SHA-1, 30 seconds, 6 digits) for a base32 key, so tests can sign in with two-factor.</summary>
public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Code(string base32Key, DateTimeOffset? at = null)
    {
        var key = Decode(base32Key);
        var counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;

        Span<byte> message = stackalloc byte[8];
        for (var i = 7; i >= 0; i--, counter >>= 8)
            message[i] = (byte)(counter & 0xFF);

        var hash = HMACSHA1.HashData(key, message);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Decode(string base32)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in base32.Replace(" ", string.Empty).TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
