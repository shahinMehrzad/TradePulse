using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Trader.Vault;

public sealed class VaultOptions
{
    public const string SectionName = "Vault";

    /// <summary>
    /// 32 random bytes, base64 (<c>openssl rand -base64 32</c>). Only this program has it: not the API, not the panel, not the database,
    /// so the encrypted private keys in the database are worth nothing to somebody who only has the database. Keep a copy somewhere safe
    /// that is not the server: without it every stored key is lost (the user would have to register new ones).
    /// </summary>
    public string MasterKey { get; set; } = "";
}

/// <summary>
/// Encrypts private keys for the database (AES-256-GCM). The key id and the owner are part of what the tag covers, so a row moved to
/// another id or another user does not decrypt: copying the encrypted key of one user into the row of another gets nothing.
/// Stored form: <c>version(1) | nonce(12) | tag(16) | ciphertext</c>.
/// </summary>
public sealed class VaultCrypto
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public VaultCrypto(IOptions<VaultOptions> options) => _key = ParseKey(options.Value.MasterKey);

    public static byte[] ParseKey(string? masterKey)
    {
        try
        {
            var key = Convert.FromBase64String(masterKey ?? "");
            if (key.Length == 32)
                return key;
        }
        catch (FormatException)
        {
            // falls through to the message below
        }

        throw new InvalidOperationException("Vault:MasterKey must be 32 random bytes in base64 (e.g. `openssl rand -base64 32`), given as the environment variable Vault__MasterKey or a secret.");
    }

    public byte[] Encrypt(byte[] plaintext, Guid keyId, Guid ownerId)
    {
        var blob = new byte[1 + NonceSize + TagSize + plaintext.Length];
        blob[0] = Version;
        var nonce = blob.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, blob.AsSpan(1 + NonceSize + TagSize), blob.AsSpan(1 + NonceSize, TagSize), Context(keyId, ownerId));
        return blob;
    }

    /// <exception cref="CryptographicException">The blob is damaged, belongs to another key id or user, or was made with another master key.</exception>
    public byte[] Decrypt(byte[] blob, Guid keyId, Guid ownerId)
    {
        if (blob.Length < 1 + NonceSize + TagSize || blob[0] != Version)
            throw new CryptographicException("Unknown format of an encrypted key.");

        var plaintext = new byte[blob.Length - 1 - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(blob.AsSpan(1, NonceSize), blob.AsSpan(1 + NonceSize + TagSize), blob.AsSpan(1 + NonceSize, TagSize), plaintext, Context(keyId, ownerId));
        return plaintext;
    }

    private static byte[] Context(Guid keyId, Guid ownerId)
    {
        var context = new byte[32];
        keyId.TryWriteBytes(context.AsSpan(0, 16));
        ownerId.TryWriteBytes(context.AsSpan(16, 16));
        return context;
    }
}
