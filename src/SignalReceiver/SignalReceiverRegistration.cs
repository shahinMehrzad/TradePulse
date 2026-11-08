using Application.Signals;
using Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SignalReceiver;

public static class SignalReceiverRegistration
{
    /// <summary>The shortest secret taken: the sender and this program share it, and anybody who has it can open trades.</summary>
    public const int MinSecretLength = 32;

    /// <summary>
    /// What the receiver is made of: the webhook's decision (<see cref="SignalIntake"/>), the price watch and the chat. The watch and the chat
    /// live as long as the program and are shared, so they get the store that makes a database context for every call; the decision lives for
    /// one request. Call it after <c>AddInfrastructureServices</c>, <c>AddBinanceExchange</c> and <c>AddTradeChat</c>.
    /// </summary>
    public static IServiceCollection AddSignalReceiver(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(ReadIntakeOptions(configuration));
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton(new WatcherOptions());
        services.AddSingleton(new AnnouncerOptions());
        services.AddSingleton(new SettlementOptions());

        // The infrastructure registers the store for one request; the shared parts need one that lives as long as the program.
        services.RemoveAll<ISignalStore>();
        services.AddScoped<SignalStore>();
        services.AddSingleton<ISignalStore, ScopedSignalStore>();
        services.AddScoped<ListedSymbols>();
        services.AddSingleton<IFollowedSymbolSource, ScopedFollowedSymbols>();

        services.AddSingleton<PriceBoard>();
        services.AddSingleton<TradeCatchUp>();
        services.AddSingleton<TradeSettlement>();
        services.AddSingleton<SignalAuthenticator>();

        services.AddSingleton<TradeAnnouncer>();
        services.AddSingleton<ITradeEvents>(provider => provider.GetRequiredService<TradeAnnouncer>());
        services.AddSingleton<IOpenedTradeSink>(provider => provider.GetRequiredService<TradeAnnouncer>());
        services.AddSingleton<PaperTradeWatcher>();
        services.AddSingleton<ITradeTracker>(provider => provider.GetRequiredService<PaperTradeWatcher>());

        services.AddScoped<SignalIntake>();

        services.AddHostedService<PaperTradeWatcherService>();
        services.AddHostedService<TradeAnnouncerService>();

        return services;
    }

    /// <summary>
    /// The secrets of the sender (<c>Signals:Webhook:Secret</c>, and <c>PreviousSecret</c> while it is being changed). A program without a good secret does
    /// not start: one that started would answer everybody with 401, and nobody would know why no signal was ever taken.
    /// </summary>
    private static SignalIntakeOptions ReadIntakeOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(SignalIntakeOptions.SectionName);
        var secret = section["Secret"];
        var previous = section["PreviousSecret"];

        if (string.IsNullOrWhiteSpace(secret) || secret.Length < MinSecretLength)
            throw new InvalidOperationException(
                $"{SignalIntakeOptions.SectionName}:Secret must be at least {MinSecretLength} characters: the secret the sender signs its requests with " +
                "(a user secret or the environment variable Signals__Webhook__Secret, e.g. `openssl rand -hex 32`).");

        if (!string.IsNullOrEmpty(previous) && previous.Length < MinSecretLength)
            throw new InvalidOperationException($"{SignalIntakeOptions.SectionName}:PreviousSecret, when it is given, must be at least {MinSecretLength} characters.");

        return new SignalIntakeOptions { Secrets = string.IsNullOrEmpty(previous) ? [secret] : [secret, previous] };
    }
}

/// <summary>The price watch, for as long as the program runs. If it ever stops on an error the program stops with it: a service that no longer watches the trades must not look alive.</summary>
internal sealed class PaperTradeWatcherService(PaperTradeWatcher watcher) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => watcher.RunAsync(stoppingToken);
}

/// <summary>The chat's messages, for as long as the program runs.</summary>
internal sealed class TradeAnnouncerService(TradeAnnouncer announcer) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => announcer.RunAsync(stoppingToken);
}
