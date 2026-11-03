using System.Security.Cryptography;

namespace Tests.Shared;

/// <summary>A fresh ECDSA P-256 key pair for a test, in the form the settings take (single-line base64), and the key itself for tests that make a token by hand.</summary>
public sealed class JwtTestKeys
{
    private JwtTestKeys(ECDsa key) => Key = key;

    public ECDsa Key { get; }

    /// <summary>Base64 PKCS#8: what <c>JwtConfigurations:PrivateKey</c> takes.</summary>
    public string PrivateKey => Convert.ToBase64String(Key.ExportPkcs8PrivateKey());

    /// <summary>Base64 SubjectPublicKeyInfo: what <c>JwtConfigurations:PublicKey</c> takes.</summary>
    public string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());

    public static JwtTestKeys New() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));
}
