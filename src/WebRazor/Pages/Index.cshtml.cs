using Microsoft.AspNetCore.Mvc;
using WebRazor.Api;

namespace WebRazor.Pages
{
    public class IndexModel(TradePulseApiClient api, SessionTokenStore store) : AdminPageModel(store)
    {
        /// <summary>The markets the filter offers, as the API names them.</summary>
        public static readonly string[] Markets = ["futures", "spot"];

        /// <summary>Only this market ("spot" or "futures"); every market when null. Anything else is ignored.</summary>
        [BindProperty(SupportsGet = true)]
        public string? Market { get; set; }

        public IReadOnlyList<DashboardCard> Cards { get; private set; } = [];

        public DateTime? GeneratedAt { get; private set; }

        public string? Error { get; private set; }

        public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
        {
            // A hand-edited address must not send the API a market it would refuse.
            Market = Markets.FirstOrDefault(m => string.Equals(m, Market, StringComparison.OrdinalIgnoreCase));

            try
            {
                var dashboard = await api.GetDashboardSeriesAsync(Market, cancellationToken);

                // The API no longer accepts this session (refresh failed or the role was removed): start over.
                if (dashboard is null)
                    return await EndSessionAsync();

                Cards = dashboard.Items;
                GeneratedAt = dashboard.GeneratedAt;
            }
            catch (HttpRequestException)
            {
                Error = "The API is not reachable right now.";
            }

            return Page();
        }
    }
}
