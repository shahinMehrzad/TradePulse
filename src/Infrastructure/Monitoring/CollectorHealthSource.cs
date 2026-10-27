using System.Text.Json;
using Application.Dtos.Collector;
using Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Monitoring;

public sealed class CollectorHealthSource(HttpClient http, IConfiguration configuration, TimeProvider timeProvider, ILogger<CollectorHealthSource> logger)
    : ICollectorHealthSource
{
    public const string UrlSetting = "Collector:HealthUrl";

    public async Task<CollectorHealthResponse> GetAsync(CancellationToken cancellationToken)
    {
        var checkedAt = timeProvider.GetUtcNow().UtcDateTime;
        var url = configuration[UrlSetting];
        if (string.IsNullOrWhiteSpace(url))
            return new CollectorHealthResponse(CollectorHealthState.NotConfigured, null, checkedAt, []);

        try
        {
            using var response = await http.GetAsync(url, cancellationToken);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

            var root = document.RootElement;
            var checks = root.GetProperty("checks").EnumerateArray().Select(ToCheck).ToList();
            return new CollectorHealthResponse(CollectorHealthState.Answered, root.GetProperty("status").GetString(), checkedAt, checks);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException
                                       or InvalidOperationException or UriFormatException)
        {
            // A shutdown (the caller's token) is not "unreachable".
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogWarning(ex, "The collector did not give a usable answer at {Setting}.", UrlSetting);
            return new CollectorHealthResponse(CollectorHealthState.Unreachable, null, checkedAt, []);
        }
    }

    private static CollectorHealthCheck ToCheck(JsonElement check)
    {
        var data = new Dictionary<string, string>();
        if (check.TryGetProperty("data", out var values) && values.ValueKind == JsonValueKind.Object)
        {
            foreach (var value in values.EnumerateObject())
                data[value.Name] = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() ?? "" : value.Value.GetRawText();
        }

        return new CollectorHealthCheck(
            check.GetProperty("name").GetString() ?? "",
            check.GetProperty("status").GetString() ?? "",
            check.TryGetProperty("description", out var description) ? description.GetString() : null,
            data);
    }
}
