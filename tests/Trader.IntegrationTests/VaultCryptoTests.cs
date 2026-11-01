using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Trader.Vault;

namespace Trader.IntegrationTests;

public class VaultCryptoTests
{
    private static readonly Guid KeyId = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();

    private static VaultCrypto Crypto(byte[]? key = null) =>
        new(Options.Create(new VaultOptions { MasterKey = Convert.ToBase64String(key ?? new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()) }));

    [Fact]
    public void WhatIsEncryptedComesBackForTheSameKeyAndOwner()
    {
        var secret = Encoding.UTF8.GetBytes("a private key, pretend");

        var blob = Crypto().Encrypt(secret, KeyId, Owner);

        Assert.Equal(secret, Crypto().Decrypt(blob, KeyId, Owner));
        Assert.DoesNotContain("private key", Encoding.UTF8.GetString(blob));
    }

    [Fact]
    public void TheSameSecretIsNeverEncryptedTheSameWayTwice()
    {
        var secret = Encoding.UTF8.GetBytes("same");

        Assert.NotEqual(Crypto().Encrypt(secret, KeyId, Owner), Crypto().Encrypt(secret, KeyId, Owner));
    }

    [Fact]
    public void ARowCopiedToAnotherKeyOrAnotherUserDoesNotDecrypt()
    {
        var blob = Crypto().Encrypt([1, 2, 3], KeyId, Owner);

        Assert.ThrowsAny<CryptographicException>(() => Crypto().Decrypt(blob, Guid.NewGuid(), Owner));
        Assert.ThrowsAny<CryptographicException>(() => Crypto().Decrypt(blob, KeyId, Guid.NewGuid()));
    }

    [Fact]
    public void ADamagedBlobDoesNotDecrypt()
    {
        var blob = Crypto().Encrypt([1, 2, 3, 4, 5], KeyId, Owner);
        blob[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => Crypto().Decrypt(blob, KeyId, Owner));
        Assert.ThrowsAny<CryptographicException>(() => Crypto().Decrypt([1, 2, 3], KeyId, Owner));
    }

    [Fact]
    public void AnotherMasterKeyDoesNotDecrypt()
    {
        var blob = Crypto().Encrypt([1, 2, 3], KeyId, Owner);

        Assert.ThrowsAny<CryptographicException>(() => Crypto(new byte[32]).Decrypt(blob, KeyId, Owner));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!!")]
    [InlineData("AAAA")] // valid base64, 3 bytes
    public void AMasterKeyThatIsNotThirtyTwoBytesIsRefused(string? key)
    {
        Assert.Throws<InvalidOperationException>(() => VaultCrypto.ParseKey(key));
    }

    [Fact]
    public void ASignatureMadeByTheProgramVerifiesWithThePublicKey()
    {
        var pair = KeyPairFactory.Create(KeyAlgorithm.Rsa);
        using var signer = SignerFactory.Create(KeyAlgorithm.Rsa, pair.PrivateKeyPkcs8);
        const string payload = "timestamp=1700000000000&recvWindow=5000";

        var signature = Convert.FromBase64String(signer.Sign(payload));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pair.PublicKeyPem);
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(payload), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.False(rsa.VerifyData(Encoding.ASCII.GetBytes(payload + "x"), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.Equal(4096, rsa.KeySize);
    }
}
