namespace WebRazor.Api;

// Wire shapes of WebApi responses. Kept here on purpose: the panel must not reference the Application layer.
public record LoginResponse(string AccessToken, DateTime ExpiresAt, string UserName, string RefreshToken);

public record AdminInfo(string UserName, string Email, string[] Roles);

public record TokenSet(string AccessToken, string RefreshToken, DateTime ExpiresAt);
