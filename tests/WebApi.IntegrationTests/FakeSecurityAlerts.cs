using Application.Notifications;

namespace WebApi.IntegrationTests;

/// <summary>The alerts the API raises in these tests (it has no Telegram bot): they are read from here. Unlike the real queue it does not hold back repeats.</summary>
public sealed class FakeSecurityAlerts : ISecurityAlerts
{
    private readonly List<SecurityAlert> _raised = [];

    public void Raise(SecurityAlert alert)
    {
        lock (_raised)
            _raised.Add(alert);
    }

    /// <summary>The alerts that name this user (the title always does).</summary>
    public IReadOnlyList<SecurityAlert> About(string userName)
    {
        lock (_raised)
            return [.. _raised.Where(a => a.Title.Contains(userName, StringComparison.Ordinal))];
    }
}
