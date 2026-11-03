namespace Domain.Entities
{
    public class RefreshToken
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Token { get; set; } = string.Empty;
        public DateTime ExpiresOn { get; set; }
        public bool IsRevoked { get; set; } = false;
        /// <summary>When a renewal used this token up (it is then also revoked). Null for a token that was revoked any other way (a sign-out, a reset): only a token that was
        /// renewed and then shown again says that a copy of the session exists.</summary>
        public DateTime? UsedAt { get; set; }
        public Guid UserId { get; set; }
        public string DeviceId { get; set; } = string.Empty;
        public string IPAddress { get; set; } = string.Empty;
        public string UserAgent { get; set; } = string.Empty;
    }
}
