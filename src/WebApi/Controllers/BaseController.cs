using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace WebApi.Controllers
{
    public abstract class BaseController : ControllerBase
    {
        /// <summary>The caller's address. Behind a trusted proxy the forwarded-headers middleware has already replaced the proxy's with the client's;
        /// the header is not read here, because anyone can send one.</summary>
        protected string GetIpAddress() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";

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
