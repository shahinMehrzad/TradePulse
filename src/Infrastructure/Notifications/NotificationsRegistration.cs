using Application.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Notifications;

public static class NotificationsRegistration
{
    /// <summary>A channel is registered only when its settings are complete, so a collector without them simply has no channel.</summary>
    public static IServiceCollection AddNotifiers(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AlertsOptions.SectionName);
        services.Configure<AlertsOptions>(section);
        var options = section.Get<AlertsOptions>() ?? new AlertsOptions();

        if (options.Telegram.IsConfigured)
        {
            // The bot token is part of the address: the client logs nothing about its requests.
            services.AddHttpClient<TelegramNotifier>(client =>
            {
                client.BaseAddress = new Uri("https://api.telegram.org/");
                client.Timeout = TimeSpan.FromSeconds(15);
            }).RemoveAllLoggers();
            services.AddTransient<INotifier>(provider => provider.GetRequiredService<TelegramNotifier>());
        }

        if (options.Email.IsConfigured)
        {
            services.AddSingleton<IEmailTransport, SmtpEmailTransport>();
            services.AddSingleton<INotifier, EmailNotifier>();
        }

        return services;
    }
}
