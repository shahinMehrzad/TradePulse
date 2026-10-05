using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace WebApi.Controllers
{
    public abstract class BaseController : ControllerBase
    {
        protected string GetIpAddress()
        {
            if (Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
            {
                var ip = forwardedFor.FirstOrDefault()?.Split(',')[0].Trim();
                if (!string.IsNullOrEmpty(ip))
                    return ip;
            }
            return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
        }

        protected string? GetUserAgent() => Request.Headers["User-Agent"].ToString();
        
        protected string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

        protected string? GetUserEmail() => User.FindFirstValue(ClaimTypes.Email);
        protected string GetDeviceId() => Request.Headers["X-Device-Id"].ToString() ?? "";

        protected string? GetRawToken()
        {            
            var authHeader = Request.Headers["Authorization"].ToString();
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return authHeader["Bearer ".Length..].Trim();
        }
    }
}
