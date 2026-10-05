namespace Application.Commands.Auth;

public record ValidateRefreshTokenCommand(Guid UserId, string RefreshToken, string DeviceId);
