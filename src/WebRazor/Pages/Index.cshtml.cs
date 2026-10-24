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

        /// <summary>One card in another time frame, as the HTML the page swaps in (see dashboard.js). Plain status codes: the script only needs to know whether it worked.</summary>
        public async Task<IActionResult> OnGetCardAsync(string exchange, string market, string symbol, string timeframe, CancellationToken cancellationToken)
        {
            try
            {
                var (detail, sessionRejected) = await api.GetDashboardDetailAsync(exchange, market, symbol, timeframe, null, cancellationToken);
                if (sessionRejected)
                    return Unauthorized();

                return detail is null ? NotFound() : Partial("_SeriesCard", detail);
            }
            catch (HttpRequestException)
            {
                return StatusCode(StatusCodes.Status502BadGateway);
            }
        }
    }
}
