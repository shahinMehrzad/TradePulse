using System.Text.RegularExpressions;
using Domain.Exceptions;

namespace Application.Commands.Symbols;

internal static partial class SymbolNames
{
    public const int MaxDescriptionLength = 100;

    /// <summary>The name a symbol is stored under: upper case, <c>BASE/QUOTE</c>, letters and digits only (<c>1000PEPE/USDT</c>).</summary>
    public static string Normalize(string? name)
    {
        var normalized = name?.Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(normalized))
            throw new ConfigurationValidationException("Name is required.");

        if (!BaseAndQuote().IsMatch(normalized))
            throw new ConfigurationValidationException("Name must look like BASE/QUOTE, e.g. BTC/USDT: letters and digits only.");

        return normalized;
    }

    [GeneratedRegex("^[A-Z0-9]{1,20}/[A-Z0-9]{1,20}$")]
    private static partial Regex BaseAndQuote();
}
