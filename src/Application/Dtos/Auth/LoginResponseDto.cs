namespace Application.Dtos.Auth;

public record LoginResponseDto(string AccessToken, DateTime ExpiresAt, string UserName, string RefreshToken);