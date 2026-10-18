namespace Application.Commands.Auth;

public record ValidateRefreshTokenCommand(string RefreshToken, string DeviceId);
