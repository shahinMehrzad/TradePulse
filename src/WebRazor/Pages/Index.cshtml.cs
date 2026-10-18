using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebRazor.Api;

namespace WebRazor.Pages
{
    public class IndexModel(TradePulseApiClient api, SessionTokenStore store) : PageModel
    {
        public AdminInfo? Admin { get; private set; }

        public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
        {
            try
            {
                Admin = await api.GetMeAsync(cancellationToken);
            }
            catch (HttpRequestException)
            {
                return RedirectToPage("/Error");
            }

            // The API no longer accepts this session (refresh failed or the role was removed): start over.
            if (Admin is null)
            {
                var sessionId = User.FindFirstValue(ApiAuthHandler.SessionClaim);
                if (sessionId is not null)
                    store.Remove(sessionId);

                await HttpContext.SignOutAsync();
                return RedirectToPage("/Login");
            }

            return Page();
        }
    }
}
