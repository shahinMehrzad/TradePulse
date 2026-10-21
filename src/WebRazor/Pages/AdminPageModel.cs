using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebRazor.Api;

namespace WebRazor.Pages;

/// <summary>Base for the signed-in pages: knows how to end a session the API no longer accepts.</summary>
public abstract class AdminPageModel(SessionTokenStore store) : PageModel
{
    /// <summary>The API refused the session (refresh failed or the role was removed): forget it and start over at the login page.</summary>
    protected async Task<IActionResult> EndSessionAsync()
    {
        var sessionId = User.FindFirstValue(ApiAuthHandler.SessionClaim);
        if (sessionId is not null)
            store.Remove(sessionId);

        await HttpContext.SignOutAsync();
        return RedirectToPage("/Login");
    }
}
