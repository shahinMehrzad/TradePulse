namespace Infrastructure.Notifications;

public sealed class AlertsOptions
{
    public const string SectionName = "Alerts";

    public string InstanceName { get; set; } = "";
    public TelegramOptions Telegram { get; set; } = new();
    public EmailOptions Email { get; set; } = new();

    public string Label => string.IsNullOrWhiteSpace(InstanceName) ? Environment.MachineName : InstanceName;
}

public sealed class TelegramOptions
{
    public string BotToken { get; set; } = "";
    public string ChatId { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);
}

public sealed class EmailOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";

    /// <summary>One address or several separated by commas or semicolons.</summary>
    public string To { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From) && !string.IsNullOrWhiteSpace(To);
}
