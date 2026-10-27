using Microsoft.AspNetCore.Mvc;
using WebRazor.Api;

namespace WebRazor.Pages;

public class CollectorHealthModel(TradePulseApiClient api, SessionTokenStore store) : AdminPageModel(store)
{
    public CollectorHealth? Health { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Components with a problem first, then by name, so what needs attention is never below the fold.</summary>
    public IReadOnlyList<CollectorHealthCheck> Checks => Health?.Checks
        .OrderBy(c => Rank(c.Status)).ThenBy(c => c.Name, StringComparer.Ordinal).ToList() ?? [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            Health = await api.GetCollectorHealthAsync(cancellationToken);

            // The API no longer accepts this session (refresh failed or the role was removed): start over.
            if (Health is null)
                return await EndSessionAsync();
        }
        catch (HttpRequestException)
        {
            Error = "The API is not reachable right now.";
        }

        return Page();
    }

    private static int Rank(string status) => status switch { "Unhealthy" => 0, "Degraded" => 1, _ => 2 };

    public static string BadgeClass(string? status) => status switch
    {
        "Healthy" => "bg-green-lt",
        "Degraded" => "bg-yellow-lt",
        "Unhealthy" => "bg-red-lt",
        _ => "bg-secondary-lt",
    };
}
