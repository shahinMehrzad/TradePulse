using Dapper;
using Trader.Audit;

namespace Trader.Trading;

/// <summary>
/// Publishes the symbols enabled for trading (the truth is <c>vault.trading_symbols</c>) as a copy in the main database, where the API
/// and the watch read them. A failure is logged and never thrown: trading does not depend on the main database, and
/// <see cref="TradedSymbolMirrorService"/> publishes again a minute later.
/// </summary>
public sealed class TradedSymbolMirror(MainDatabase main, TradingRepository repository, TimeProvider time, ILogger<TradedSymbolMirror> logger)
{
    public async Task PublishAsync(CancellationToken cancellationToken)
    {
        try
        {
            var enabled = await repository.EnabledSymbolsAsync(cancellationToken);
            var at = DateTime.SpecifyKind(time.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified); // timestamp without zone, always UTC

            await using var connection = await main.Source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM config.traded_symbols", transaction: transaction, cancellationToken: cancellationToken));
            foreach (var symbol in enabled)
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO config.traded_symbols (symbol, changed_at) VALUES (@symbol, @at)",
                    new { symbol = symbol.ToUpperInvariant(), at }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not publish the enabled symbols to the main database; the watch keeps the last copy.");
        }
    }
}

/// <summary>Publishes at start and every minute, so a main database that was down at the moment of a change catches up.</summary>
public sealed class TradedSymbolMirrorService(TradedSymbolMirror mirror) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            await mirror.PublishAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
