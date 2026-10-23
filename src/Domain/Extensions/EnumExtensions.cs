namespace Domain.Extensions;

public static class EnumExtensions
{    
    public static string ToRouteName<T>(this T value) where T : struct, Enum =>
        value.ToString().TrimStart('_').ToLowerInvariant();
        
    public static bool TryParseRouteName<T>(string? value, out T result) where T : struct, Enum
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value) || value.Contains(','))
            return false;

        // Time frame members are named "_4H", so the URL's "4h" is tried with the underscore too.
        foreach (var candidate in new[] { value, "_" + value })
        {
            if (!candidate.All(char.IsDigit) && Enum.TryParse(candidate, ignoreCase: true, out T parsed) && Enum.IsDefined(parsed))
            {
                result = parsed;
                return true;
            }
        }

        return false;
    }
}
