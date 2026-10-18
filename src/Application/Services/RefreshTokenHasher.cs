using System.Security.Cryptography;
using System.Text;

namespace Application.Services;

/// <summary>
/// Refresh tokens are stored as a SHA-256 hash, so a leaked database does not hand out live sessions. A fast hash is
/// enough here because the token is 256 bits of randomness, not a human-chosen password.
/// </summary>
public static class RefreshTokenHasher
{
    public static string Hash(string token) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
