using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebRazor.Api;

namespace WebRazor.Pages;

public class LogoutModel(SessionTokenStore store) : PageModel
{
    // POST only (with the antiforgery token the layout's form carries): a link or image can't sign the admin out.
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        var sessionId = User.FindFirstValue(ApiAuthHandler.SessionClaim);
        if (sessionId is not null)
            store.Remove(sessionId);

        await HttpContext.SignOutAsync();
        return RedirectToPage("/Login");
    }
}
