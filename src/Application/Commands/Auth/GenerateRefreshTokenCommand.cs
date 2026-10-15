namespace Application.Commands.Auth
{
    public record GenerateRefreshTokenCommand(Guid UserId, string? DeviceId, string? IpAddress, string? UserAgent, int DurationInDays = 7);
}
