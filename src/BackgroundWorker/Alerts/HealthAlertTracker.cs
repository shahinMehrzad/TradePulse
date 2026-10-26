using Application.Notifications;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BackgroundWorker.Alerts;

/// <summary>
/// Turns the health reports into messages: one when a check has really changed, and one when it is back. "Really" means the new
/// status was seen in <c>confirmations</c> reports in a row, so a check that flickers between two reports says nothing. Every check
/// is judged on its own (a freshness problem is announced even if the database is already unhealthy), and the checks that changed in
/// the same report share one message. A check that is healthy from the first report is never announced.
/// </summary>
public sealed class HealthAlertTracker(int confirmations)
{
    private readonly Dictionary<string, HealthStatus> _announced = [];
    private readonly Dictionary<string, (HealthStatus Status, int Reports)> _pending = [];

    public Alert? Observe(HealthReport report)
    {
        var changes = new List<(string Name, HealthReportEntry Entry)>();

        foreach (var (name, entry) in report.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (entry.Status == _announced.GetValueOrDefault(name, HealthStatus.Healthy))
            {
                _pending.Remove(name);
                continue;
            }

            var reports = _pending.TryGetValue(name, out var seen) && seen.Status == entry.Status ? seen.Reports + 1 : 1;
            if (reports < confirmations)
            {
                _pending[name] = (entry.Status, reports);
                continue;
            }

            _pending.Remove(name);
            _announced[name] = entry.Status;
            changes.Add((name, entry));
        }

        if (changes.Count == 0)
            return null;

        var title = changes.All(c => c.Entry.Status == HealthStatus.Healthy) ? "Collector is healthy again"
            : changes.Any(c => c.Entry.Status == HealthStatus.Unhealthy) ? "Collector problem"
            : "Collector warning";

        return new Alert(title, string.Join("\n", changes.Select(c => Describe(c.Name, c.Entry))));
    }

    private static string Describe(string name, HealthReportEntry entry) => entry.Status switch
    {
        HealthStatus.Healthy => $"✅ {name}: healthy again",
        HealthStatus.Degraded => $"⚠️ {name}: degraded - {entry.Description}",
        _ => $"🔴 {name}: unhealthy - {entry.Description}",
    };
}
