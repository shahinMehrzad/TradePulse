using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Entities;
using Domain.Enums;
using Domain.Extensions;
using Domain.ValueObjects;

namespace Application.Signals;

/// <summary>
/// Reads the body of a signal (the webhook contract) into a <see cref="TradeSignal"/>. It only checks that the body says what the contract
/// asks for, in the shape it asks for: whether the signal is worth taking is <see cref="Domain.Services.SignalValidator"/>'s. An error
/// names the field and never repeats what was sent.
/// </summary>
public static partial class SignalParser
{
    public const int MaxSignalIdLength = 96;

    [GeneratedRegex(@"^[A-Z0-9]{2,20}/[A-Z0-9]{2,20}$")]
    private static partial Regex SymbolPattern();

    // A time without a zone is ambiguous, and the contract is UTC: it must end in Z or an offset.
    [GeneratedRegex(@"(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex ZonePattern();

    public static bool TryParse(string body, [NotNullWhen(true)] out TradeSignal? signal, [NotNullWhen(false)] out string? error)
    {
        signal = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
        }
        catch (JsonException)
        {
            error = "The body is not valid JSON.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "The body must be a JSON object.";
                return false;
            }

            var fields = root.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);

            if (!Text(fields, "signalId", out var signalId, out error) || signalId.Length > MaxSignalIdLength) { error ??= "signalId is too long."; return false; }
            if (!Text(fields, "strategy", out var strategy, out error)) return false;
            if (!SignalStrategyConfiguration.IsValidName(strategy)) { error = "strategy is not a valid label."; return false; }
            if (!Text(fields, "symbol", out var symbol, out error)) return false;
            if (!SymbolPattern().IsMatch(symbol)) { error = "symbol must look like BTC/USDT."; return false; }
            if (!Text(fields, "timeframe", out var timeFrameText, out error)) return false;
            if (!EnumExtensions.TryParseRouteName<TimeFrameEnum>(timeFrameText, out var timeFrame)) { error = "timeframe is not a known time frame."; return false; }

            if (!Choice<MarketTypeEnum>(fields, "market", required: true, default, out var market, out error)) return false;
            if (!Choice<PositionSideEnum>(fields, "side", required: true, default, out var side, out error)) return false;
            if (!Choice<ExchangeEnum>(fields, "exchange", required: false, ExchangeEnum.Binance, out var exchange, out error)) return false;

            if (!Number(fields, "leverage", out var leverage, out error)) return false;
            if (!Number(fields, "takeProfit", out var takeProfit, out error)) return false;
            if (!Number(fields, "stopLoss", out var stopLoss, out error)) return false;
            if (!Number(fields, "signalPrice", out var signalPrice, out error)) return false;

            if (!Time(fields, "candleOpenTime", out var candleOpenTime, out error)) return false;
            if (!Time(fields, "candleCloseTime", out var candleCloseTime, out error)) return false;
            if (!Time(fields, "validUntil", out var validUntil, out error)) return false;

            TimeSpan? maxDuration = null;
            if (fields.TryGetValue("maxHours", out var maxHours) && maxHours.ValueKind != JsonValueKind.Null)
            {
                if (maxHours.ValueKind != JsonValueKind.Number || !maxHours.TryGetDecimal(out var hours) || hours <= 0 || hours > 24 * 365)
                {
                    error = "maxHours must be a number of hours above 0, or left out.";
                    return false;
                }

                maxDuration = TimeSpan.FromHours((double)hours);
            }

            signal = new TradeSignal(
                signalId, strategy, market, symbol, side, leverage, takeProfit, stopLoss, signalPrice, timeFrame,
                candleOpenTime, candleCloseTime, validUntil, maxDuration, exchange);
            error = null;
            return true;
        }
    }

    private static bool Text(Dictionary<string, JsonElement> fields, string name, [NotNullWhen(true)] out string? value, [NotNullWhen(false)] out string? error)
    {
        if (fields.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text)
        {
            value = text;
            error = null;
            return true;
        }

        value = null;
        error = $"{name} is missing or is not text.";
        return false;
    }

    private static bool Number(Dictionary<string, JsonElement> fields, string name, out decimal value, [NotNullWhen(false)] out string? error)
    {
        if (fields.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value) && value > 0)
        {
            error = null;
            return true;
        }

        value = 0;
        error = $"{name} is missing or is not a number above 0.";
        return false;
    }

    private static bool Time(Dictionary<string, JsonElement> fields, string name, out DateTime value, [NotNullWhen(false)] out string? error)
    {
        if (fields.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.String && element.GetString() is { } text
            && ZonePattern().IsMatch(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            value = parsed.UtcDateTime;
            error = null;
            return true;
        }

        value = default;
        error = $"{name} is missing or is not a time with a zone (like 2026-11-07T04:00:00Z).";
        return false;
    }

    /// <summary>An enum given by its name (never by its number, which would let any value through).</summary>
    private static bool Choice<T>(Dictionary<string, JsonElement> fields, string name, bool required, T fallback, out T value, [NotNullWhen(false)] out string? error) where T : struct, Enum
    {
        if (!fields.TryGetValue(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            value = fallback;
            error = required ? $"{name} is missing." : null;
            return !required;
        }

        if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text
            && !text.All(char.IsDigit) && Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value))
        {
            error = null;
            return true;
        }

        value = default;
        error = $"{name} is not one of the known values.";
        return false;
    }
}
