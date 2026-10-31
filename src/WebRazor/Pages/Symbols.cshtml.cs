using Microsoft.AspNetCore.Mvc;
using WebRazor.Api;

namespace WebRazor.Pages;

/// <summary>
/// The symbols the collector can be told about: a searchable, paged list with the markets each is listed on, where the admin adds a
/// symbol, changes its description and removes one that is listed nowhere.
/// </summary>
public class SymbolsModel(TradePulseApiClient api, SessionTokenStore store) : AdminPageModel(store)
{
    public const int PageSize = 10;

    public IReadOnlyList<SymbolRow> Symbols { get; private set; } = [];

    /// <summary>Which page of the list is shown. Kept (with <see cref="Keyword"/>) through the edit, save and remove round trips.</summary>
    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    /// <summary>Narrows the list; the API matches it against the name, the description and the exchanges' names for the symbol.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Keyword { get; set; }

    public bool HasPrevious => PageNumber > 1;

    public bool HasNext { get; private set; }

    /// <summary>How many symbols match the search (all of them without one), as the API counts them.</summary>
    public int TotalItems { get; private set; }

    public int TotalPages { get; private set; }

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public string? Saved { get; private set; }

    [BindProperty]
    public SymbolForm Input { get; set; } = new();

    /// <summary>True while the form changes an existing symbol instead of adding one.</summary>
    public bool IsEditing => Input.Id != 0;

    // No validation attributes: the API refuses what is missing or wrong with a reason of its own.
    public class SymbolForm
    {
        public int Id { get; set; }

        /// <summary>BASE/QUOTE. Fixed once the symbol exists: the collector's tables are named after the exchange's name, not this one.</summary>
        public string? Name { get; set; }

        public string? Description { get; set; }
    }

    /// <summary>The Edit button links here with <c>?edit=id</c>, which fills the form with that symbol.</summary>
    public async Task<IActionResult> OnGetAsync(int? edit, CancellationToken cancellationToken)
    {
        // A hand-edited address must not reach the API with a page it would refuse.
        PageNumber = Math.Max(1, PageNumber);

        var result = await LoadAsync(cancellationToken);
        if (result is not null)
            return result;

        if (edit is { } id)
        {
            var symbol = Symbols.FirstOrDefault(s => s.Id == id);
            if (symbol is null)
                Errors = ["That symbol does not exist (anymore)."];
            else
                Input = new SymbolForm { Id = symbol.Id, Name = symbol.Name, Description = symbol.Description };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        PageNumber = Math.Max(1, PageNumber);

        var result = await CallAsync(() => IsEditing
            ? api.UpdateSymbolAsync(Input.Id, Input.Description, cancellationToken)
            : api.AddSymbolAsync(Input.Name, Input.Description, cancellationToken));

        if (result.SessionRejected)
            return await EndSessionAsync();

        if (result.Success)
        {
            Saved = IsEditing ? "The symbol was changed." : "The symbol was added.";
            ResetForm();
        }
        else
        {
            Errors = result.Errors;
        }

        return await LoadAsync(cancellationToken) ?? Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken cancellationToken)
    {
        // The form is not part of this request. Binding still fills Input.Id from the "id" of the removed row, which would turn the
        // next "add" into a change of a row that is gone.
        ResetForm();
        PageNumber = Math.Max(1, PageNumber);

        var result = await CallAsync(() => api.DeleteSymbolAsync(id, cancellationToken));
        if (result.SessionRejected)
            return await EndSessionAsync();

        if (result.Success)
            Saved = "The symbol was removed.";
        else
            Errors = result.Errors;

        return await LoadAsync(cancellationToken) ?? Page();
    }

    /// <summary>
    /// Back to an empty "add" form. The posted values have to go too: a tag helper shows what was posted in preference to the model,
    /// so resetting <see cref="Input"/> alone would leave the form still pointing at the old row.
    /// </summary>
    private void ResetForm()
    {
        ModelState.Clear();
        Input = new SymbolForm();
    }

    /// <summary>A write that could not reach the API is shown as an error, not as a crash.</summary>
    private static async Task<ApiResult> CallAsync(Func<Task<ApiResult>> write)
    {
        try
        {
            return await write();
        }
        catch (HttpRequestException)
        {
            return new ApiResult(false, ["The API is not reachable right now."]);
        }
    }

    /// <returns>A redirect when the session is over, otherwise null (the page then renders with whatever could be loaded).</returns>
    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var page = await api.GetSymbolsPageAsync(PageNumber, PageSize, Keyword, cancellationToken);

            // Removing the last row of the last page leaves that page empty: step back to the one before it.
            while (page is { Items.Count: 0 } && PageNumber > 1)
                page = await api.GetSymbolsPageAsync(--PageNumber, PageSize, Keyword, cancellationToken);

            if (page is null)
                return await EndSessionAsync();

            Symbols = page.Items;
            HasNext = page.HasNext;
            TotalItems = page.TotalItems;
            TotalPages = page.TotalPages;
        }
        catch (HttpRequestException)
        {
            Errors = [.. Errors, "The API is not reachable right now."];
        }

        return null;
    }
}
