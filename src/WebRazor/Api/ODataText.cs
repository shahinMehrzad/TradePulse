namespace WebRazor.Api;

/// <summary>Pieces of an OData <c>$filter</c> that hold text, shared by the lists of the panel.</summary>
public static class ODataText
{
    /// <summary>A text in an OData filter: a quote inside it is written twice.</summary>
    public static string Quote(string text) => $"'{text.Replace("'", "''")}'";

    /// <summary>Case-insensitive "contains" of the text in any of the fields.</summary>
    public static string ContainsAny(string text, params string[] fields)
    {
        var term = Quote(text.Trim().ToLowerInvariant());
        return "(" + string.Join(" or ", fields.Select(field => $"contains(tolower({field}),{term})")) + ")";
    }
}
