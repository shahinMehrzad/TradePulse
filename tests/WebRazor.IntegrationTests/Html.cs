using System.Net;
using System.Text.RegularExpressions;

namespace WebRazor.IntegrationTests;

/// <summary>Just enough reading of a rendered page for the assertions of these tests (not an HTML parser).</summary>
internal static partial class Html
{
    /// <summary>The <c>href</c> of every link whose text is exactly <paramref name="text"/> (the text may carry an arrow after it), decoded.</summary>
    public static IReadOnlyList<string> LinksNamed(string html, string text) =>
        [.. AnchorRegex().Matches(html)
            .Where(m => Plain(m.Groups["text"].Value).StartsWith(text, StringComparison.Ordinal))
            .Select(m => WebUtility.HtmlDecode(m.Groups["href"].Value))];

    /// <summary>The <c>action</c> of every form, decoded.</summary>
    public static IReadOnlyList<string> FormActions(string html) =>
        [.. FormRegex().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups["action"].Value))];

    /// <summary>The query of an address as a dictionary (the last of a repeated name wins).</summary>
    public static Dictionary<string, string> Query(string address)
    {
        var query = address.Contains('?') ? address[(address.IndexOf('?') + 1)..] : "";
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");
    }

    /// <summary>The text of the option that is selected in the select of that name, or null.</summary>
    public static string? SelectedOption(string html, string selectName)
    {
        var select = Regex.Match(html, $"<select[^>]*name=\"{Regex.Escape(selectName)}\"[^>]*>(?<body>.*?)</select>", RegexOptions.Singleline);
        var chosen = Regex.Match(select.Groups["body"].Value, "<option[^>]*selected[^>]*>(?<text>[^<]*)</option>");
        return chosen.Success ? WebUtility.HtmlDecode(chosen.Groups["text"].Value).Trim() : null;
    }

    /// <summary>The page's text without its tags and with the white space squeezed.</summary>
    public static string Text(string html) =>
        WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase), "<[^>]+>", " "))
            .Pipe(text => Regex.Replace(text, "\\s+", " ").Trim());

    private static string Plain(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")).Trim();

    private static T2 Pipe<T1, T2>(this T1 value, Func<T1, T2> next) => next(value);

    [GeneratedRegex("<a\\b[^>]*\\bhref=\"(?<href>[^\"]*)\"[^>]*>(?<text>.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex("<form\\b[^>]*\\baction=\"(?<action>[^\"]*)\"[^>]*>", RegexOptions.Singleline)]
    private static partial Regex FormRegex();
}
