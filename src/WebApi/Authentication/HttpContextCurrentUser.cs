using System.Security.Claims;
using Application.Interfaces;

namespace WebApi.Authentication;

/// <summary>The user name in the access token of the request being handled; <c>unknown</c> when there is none.</summary>
public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string Name => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name) is { Length: > 0 } name ? name : "unknown";
}
