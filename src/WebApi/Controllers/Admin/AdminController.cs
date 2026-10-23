using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace WebApi.Controllers.Admin;

/// <summary>
/// Management endpoints for the admin panel (WebRazor). Every action needs the admin role,
/// so a valid token of an ordinary user is a 403, not a 200.
/// </summary>
[Route("api/[controller]")]
public class AdminController : AdminControllerBase
{
    /// <summary>Who the token belongs to. The panel calls it right after login to confirm the user is an admin.</summary>
    [HttpGet("me")]
    public AdminInfo Me() => new(
        User.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
        User.FindFirstValue(ClaimTypes.Email) ?? string.Empty,
        User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray());
}

public record AdminInfo(string UserName, string Email, string[] Roles);
