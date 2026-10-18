namespace WebRazor.Api;

public class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>Base address of WebApi, e.g. <c>https://localhost:7006</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;
}
