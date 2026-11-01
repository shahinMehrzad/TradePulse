using Dapper;
using Npgsql;

namespace Trader.Trading;

public static class OrderStatus
{
    /// <summary>Written before Binance is asked, so the daily budget is spent even if this program dies in between.</summary>
    public const string Pending = "Pending";

    public const string Placed = "Placed";

    /// <summary>Binance said no (or could not be reached before the order left): nothing was opened.</summary>
    public const string Failed = "Failed";

    /// <summary>The order was sent but no answer came back: it may exist at Binance. Counts against the budget; check the position there.</summary>
    public const string Unknown = "Unknown";
}

public sealed record OrderRow(
    Guid Id, Guid OwnerUserId, Guid KeyId, string Symbol, string Side, decimal Quantity, bool ReduceOnly, decimal NotionalUsd,
    string Status, long? ExchangeOrderId, string? Error, DateTime CreatedAt);

/// <summary>Orders and the halt switch of each user (schema <c>vault</c>, like the keys). Times are <c>timestamp</c>, always UTC.</summary>
public sealed class TradingRepository(NpgsqlDataSource dataSource)
{
    private const string Columns = """
        id AS Id, owner_user_id AS OwnerUserId, key_id AS KeyId, symbol AS Symbol, side AS Side, quantity AS Quantity, reduce_only AS ReduceOnly,
        notional_usd AS NotionalUsd, status AS Status, exchange_order_id AS ExchangeOrderId, error AS Error, created_at AS CreatedAt
        """;

    public static async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            CREATE SCHEMA IF NOT EXISTS vault;
            CREATE TABLE IF NOT EXISTS vault.orders (
                id                uuid PRIMARY KEY,
                owner_user_id     uuid NOT NULL,
                key_id            uuid NOT NULL,
                symbol            text NOT NULL,
                side              text NOT NULL,
                quantity          numeric NOT NULL,
                reduce_only       boolean NOT NULL,
                notional_usd      numeric NOT NULL,
                status            text NOT NULL,
                exchange_order_id bigint NULL,
                error             text NULL,
                created_at        timestamp NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_orders_owner_created ON vault.orders (owner_user_id, created_at DESC);
            CREATE TABLE IF NOT EXISTS vault.trading_state (
                owner_user_id uuid PRIMARY KEY,
                halted        boolean NOT NULL,
                changed_at    timestamp NOT NULL
            );
            """, cancellationToken: cancellationToken));
    }

    public async Task<bool> IsHaltedAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT coalesce((SELECT halted FROM vault.trading_state WHERE owner_user_id = @owner), false)", new { owner }, cancellationToken: cancellationToken));
    }

    public async Task SetHaltedAsync(Guid owner, bool halted, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO vault.trading_state (owner_user_id, halted, changed_at) VALUES (@owner, @halted, @at)
            ON CONFLICT (owner_user_id) DO UPDATE SET halted = @halted, changed_at = @at
            """, new { owner, halted, at = Unspecified(nowUtc) }, cancellationToken: cancellationToken));
    }

    /// <summary>What the user has opened since <paramref name="dayStartUtc"/> (failed orders excluded, closing orders never counted).</summary>
    public async Task<decimal> OpenedSinceAsync(Guid owner, DateTime dayStartUtc, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<decimal>(new CommandDefinition("""
            SELECT coalesce(sum(notional_usd), 0) FROM vault.orders
            WHERE owner_user_id = @owner AND created_at >= @since AND NOT reduce_only AND status <> @failed
            """, new { owner, since = Unspecified(dayStartUtc), failed = OrderStatus.Failed }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Writes the order as Pending, but only if it still fits in the day's budget; the check and the insert are one statement, so two
    /// orders sent at the same moment cannot both squeeze in.
    /// </summary>
    /// <returns>False when the budget would be exceeded.</returns>
    public async Task<bool> TryReserveAsync(OrderRow order, decimal dailyCapUsd, DateTime dayStartUtc, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO vault.orders (id, owner_user_id, key_id, symbol, side, quantity, reduce_only, notional_usd, status, created_at)
            SELECT @Id, @OwnerUserId, @KeyId, @Symbol, @Side, @Quantity, @ReduceOnly, @NotionalUsd, @Status, @CreatedAt
            WHERE @ReduceOnly OR (
                SELECT coalesce(sum(notional_usd), 0) FROM vault.orders
                WHERE owner_user_id = @OwnerUserId AND created_at >= @since AND NOT reduce_only AND status <> @failed) + @NotionalUsd <= @cap
            """,
            new
            {
                order.Id, order.OwnerUserId, order.KeyId, order.Symbol, order.Side, order.Quantity, order.ReduceOnly, order.NotionalUsd,
                order.Status, CreatedAt = Unspecified(order.CreatedAt), since = Unspecified(dayStartUtc), failed = OrderStatus.Failed, cap = dailyCapUsd
            }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task CompleteAsync(Guid id, string status, long? exchangeOrderId, string? error, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE vault.orders SET status = @status, exchange_order_id = @exchangeOrderId, error = @error WHERE id = @id",
            new { id, status, exchangeOrderId, error }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<OrderRow>> RecentAsync(Guid owner, int count, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<OrderRow>(new CommandDefinition(
            $"SELECT {Columns} FROM vault.orders WHERE owner_user_id = @owner ORDER BY created_at DESC LIMIT @count",
            new { owner, count }, cancellationToken: cancellationToken)))
            .Select(row => row with { CreatedAt = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc) }).ToList();
    }

    // Npgsql refuses Kind=Utc for a timestamp column.
    private static DateTime Unspecified(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
}
