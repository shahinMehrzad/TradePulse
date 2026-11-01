using System.Security.Cryptography;

namespace Infrastructure.Identity;

/// <summary>The one-time password an administrator hands to a new user. Random, and it satisfies the password rules of the panel.</summary>
internal static class TemporaryPassword
{
    // No look-alikes (0/O, 1/l/I): the password is read off a screen and typed.
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!@#$%^&*-_=+?";

    public static string Generate(int length = 20)
    {
        const string all = Upper + Lower + Digits + Symbols;

        var chars = new char[length];
        chars[0] = Pick(Upper);
        chars[1] = Pick(Lower);
        chars[2] = Pick(Digits);
        chars[3] = Pick(Symbols);
        for (var i = 4; i < length; i++)
            chars[i] = Pick(all);

        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
