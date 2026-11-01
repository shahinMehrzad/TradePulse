using System.Security.Cryptography;
using System.Text;

namespace Trader.Vault;

/// <summary>
/// How a key signs its requests. RSA is what Binance documents for the Futures REST API. Ed25519 is documented for its WebSocket API and
/// for Spot, not (yet) for Futures REST; it can be added here as one more member when that is confirmed. The rest of the program
/// only knows <see cref="IRequestSigner"/>.
/// </summary>
public enum KeyAlgorithm { Rsa }

public sealed record GeneratedKeyPair(byte[] PrivateKeyPkcs8, string PublicKeyPem);

public static class KeyPairFactory
{
    public static GeneratedKeyPair Create(KeyAlgorithm algorithm)
    {
        switch (algorithm)
        {
            case KeyAlgorithm.Rsa:
                using (var rsa = RSA.Create(4096))
                    return new GeneratedKeyPair(rsa.ExportPkcs8PrivateKey(), rsa.ExportSubjectPublicKeyInfoPem());
            default:
                throw new NotSupportedException($"Key algorithm {algorithm} is not supported.");
        }
    }
}

/// <summary>Signs the query string of a Binance request. Holds the private key only for as long as it lives; dispose it as soon as the request is signed.</summary>
public interface IRequestSigner : IDisposable
{
    /// <returns>The signature, base64. The caller URL-encodes it.</returns>
    string Sign(string payload);
}

public sealed class RsaRequestSigner : IRequestSigner
{
    private readonly RSA _rsa = RSA.Create();

    public RsaRequestSigner(byte[] privateKeyPkcs8) => _rsa.ImportPkcs8PrivateKey(privateKeyPkcs8, out _);

    /// <summary>RSASSA-PKCS1-v1_5 with SHA-256, as the Futures documentation describes.</summary>
    public string Sign(string payload) =>
        Convert.ToBase64String(_rsa.SignData(Encoding.ASCII.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    public void Dispose() => _rsa.Dispose();
}

public static class SignerFactory
{
    /// <summary>The caller clears <paramref name="privateKeyPkcs8"/> afterwards; the signer keeps its own copy inside the crypto provider.</summary>
    public static IRequestSigner Create(KeyAlgorithm algorithm, byte[] privateKeyPkcs8) => algorithm switch
    {
        KeyAlgorithm.Rsa => new RsaRequestSigner(privateKeyPkcs8),
        _ => throw new NotSupportedException($"Key algorithm {algorithm} is not supported.")
    };
}
