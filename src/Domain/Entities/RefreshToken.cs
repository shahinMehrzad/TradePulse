namespace Domain.Entities
{
    public class RefreshToken
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Token { get; set; } = string.Empty;
        public DateTime ExpiresOn { get; set; }
        public bool IsRevoked { get; set; } = false;
        public Guid UserId { get; set; }
        public string DeviceId { get; set; } = string.Empty;
        public string IPAddress { get; set; } = string.Empty;
        public string UserAgent { get; set; } = string.Empty;
    }
}
