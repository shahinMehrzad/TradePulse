namespace Domain.Extensions;

public static class EnumExtensions
{    
    public static string ToRouteName<T>(this T value) where T : struct, Enum =>
        value.ToString().TrimStart('_').ToLowerInvariant();
}
