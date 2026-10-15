namespace Application.Dtos.Auth;

public record AuthResponseDto(string AccessToken, DateTime ExpiresAt, string UserName, Guid UserId, int RefreshTokenDurationInDays);