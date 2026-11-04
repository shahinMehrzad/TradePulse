namespace Infrastructure.Configurations
{
    /// <summary>
    /// How tokens are signed and checked. Tokens are signed with ECDSA P-256 (ES256): the API holds the private key and is the only one
    /// that can make a token; the programs that only have to believe a token (the key vault) hold the public key, which cannot make one.
    /// Both keys are single-line base64 (made by <c>deploy/make-jwt-keys.sh</c>), so they fit an environment variable.
    /// </summary>
    public class JwtConfigurations
    {
        /// <summary>Base64 of the PKCS#8 private key. Only the API has it. Nothing else may.</summary>
        public string PrivateKey { get; set; } = string.Empty;

        /// <summary>Base64 of the SubjectPublicKeyInfo public key. The API may leave it out (it is the public half of its private key); a program that only checks tokens needs it.</summary>
        public string PublicKey { get; set; } = string.Empty;

        public string Issuer { get; set; } = string.Empty;
        public string Audience { get; set; } = string.Empty;
        public int DurationInMinutes { get; set; } = 60;
        public int RefreshTokenDurationInDays { get; set; } = 7;
    }
}
