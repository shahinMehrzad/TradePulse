using Microsoft.AspNetCore.Mvc;
using WebRazor.Api;

namespace WebRazor.Pages;

public class AuditModel(TradePulseApiClient api, SessionTokenStore store) : AdminPageModel(store)
{
    public static readonly string[] Actions = ["Added", "Updated", "Removed"];

    /// <summary>The kinds of setting the log talks about, with the words the page shows for them.</summary>
    public static readonly IReadOnlyDictionary<string, string> EntityTypes = new Dictionary<string, string>
    {
        ["CandleSeries"] = "Candle series",
        ["SymbolData"] = "Funding & liquidations",
        ["User"] = "Users",
        ["ExchangeKey"] = "Exchange keys",
    };

    public static readonly int[] PageSizes = [10, 25, 50, 100];

    // Everything the page filters on is in the address, so a filtered view can be refreshed, bookmarked or sent to someone.
    [BindProperty(SupportsGet = true)] public string? Action { get; set; }
    [BindProperty(SupportsGet = true)] public string? EntityType { get; set; }
    /// <summary>Called <c>User</c> in the address; the property is not, because <c>PageModel.User</c> is the signed-in user.</summary>
    [BindProperty(Name = "User", SupportsGet = true)] public string? UserFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
    [BindProperty(SupportsGet = true)] public bool NewestFirst { get; set; } = true;
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = 25;

    public IReadOnlyList<AuditEntry> Entries { get; private set; } = [];

    public int TotalItems { get; private set; }

    public int TotalPages { get; private set; }

    public bool HasPrevious => PageNumber > 1;

    public bool HasNext => PageNumber < TotalPages;

    public string? Error { get; private set; }

    public bool IsFiltered => Action is not null || EntityType is not null || !string.IsNullOrWhiteSpace(UserFilter)
        || !string.IsNullOrWhiteSpace(Search) || From is not null || To is not null;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        // A hand-edited address must not send the API something it would refuse.
        Action = Actions.FirstOrDefault(a => string.Equals(a, Action, StringComparison.OrdinalIgnoreCase));
        EntityType = EntityTypes.Keys.FirstOrDefault(t => string.Equals(t, EntityType, StringComparison.OrdinalIgnoreCase));
        if (!PageSizes.Contains(PageSize))
            PageSize = 25;
        if (PageNumber < 1)
            PageNumber = 1;
        if (From is not null && To is not null && From > To)
            (From, To) = (To, From);

        try
        {
            var page = await api.GetAuditLogAsync(new AuditQuery(Action, EntityType, UserFilter, Search, From, To, NewestFirst, PageNumber, PageSize), cancellationToken);

            // The API no longer accepts this session (refresh failed or the role was removed): start over.
            if (page is null)
                return await EndSessionAsync();

            Entries = page.Items;
            TotalItems = page.TotalItems;
            TotalPages = page.TotalPages;
        }
        catch (HttpRequestException)
        {
            Error = "The API is not reachable right now.";
        }

        return Page();
    }

    /// <summary>The address of this view, optionally on another page or in the other order. Empty filters are left out.</summary>
    public Dictionary<string, string?> RouteValues(int? page = null, bool? newestFirst = null) => new()
    {
        ["Action"] = Action,
        ["EntityType"] = EntityType,
        ["User"] = string.IsNullOrWhiteSpace(UserFilter) ? null : UserFilter,
        ["Search"] = string.IsNullOrWhiteSpace(Search) ? null : Search,
        ["From"] = From?.ToString("yyyy-MM-dd"),
        ["To"] = To?.ToString("yyyy-MM-dd"),
        ["NewestFirst"] = (newestFirst ?? NewestFirst).ToString(),
        ["PageSize"] = PageSize.ToString(),
        ["PageNumber"] = (page ?? 1).ToString(),
    };

    public static string BadgeClass(string action) => action switch
    {
        "Added" => "bg-green-lt",
        "Removed" => "bg-red-lt",
        _ => "bg-blue-lt",
    };

    public static string EntityName(string entityType) => EntityTypes.TryGetValue(entityType, out var name) ? name : entityType;
}
