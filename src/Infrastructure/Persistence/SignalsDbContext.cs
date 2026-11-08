using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.Persistence;

/// <summary>
/// The signals that other programs send, the paper trades opened from them and the log of both. A schema of its own: the API makes it and
/// owns it (its migrations), the service that takes signals writes only these tables, and the program that makes signals can read none of it.
/// </summary>
public class SignalsDbContext(DbContextOptions<SignalsDbContext> options) : DbContext(options)
{
    private static readonly ValueConverter<DateTime, DateTime> UtcToTimestamp = new(
        value => DateTime.SpecifyKind(value, DateTimeKind.Unspecified),
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public DbSet<ReceivedSignal> Signals => Set<ReceivedSignal>();
    public DbSet<PaperTrade> PaperTrades => Set<PaperTrade>();
    public DbSet<SignalEvent> Events => Set<SignalEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema("signals");

        // Time columns are timestamp (no zone), always UTC. Npgsql refuses Kind=Utc for them, so the kind is dropped on the way in and put back
        // on the way out; the same applies to the values a query is given.
        builder.Entity<ReceivedSignal>(e =>
        {
            e.ToTable("signals", "signals", t => t.HasCheckConstraint("CK_signals_leverage", "\"Leverage\" >= 1"));

            e.Property(x => x.SignalId).HasMaxLength(96);
            e.Property(x => x.Strategy).HasMaxLength(32);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Exchange).HasConversion<int>().HasDefaultValue(ExchangeEnum.Binance).HasSentinel(ExchangeEnum.Binance);
            e.Property(x => x.Market).HasConversion<int>();
            e.Property(x => x.Side).HasConversion<int>();
            e.Property(x => x.TimeFrame).HasConversion<int>();
            e.Property(x => x.Rejection).HasConversion<int?>();
            e.Property(x => x.Leverage).HasPrecision(18, 4);
            e.Property(x => x.TakeProfit).HasPrecision(28, 8);
            e.Property(x => x.StopLoss).HasPrecision(28, 8);
            e.Property(x => x.SignalPrice).HasPrecision(28, 8);

            e.Property(x => x.CandleOpenTime).HasColumnType("timestamp").HasConversion(UtcToTimestamp);
            e.Property(x => x.CandleCloseTime).HasColumnType("timestamp").HasConversion(UtcToTimestamp);
            e.Property(x => x.ValidUntil).HasColumnType("timestamp").HasConversion(UtcToTimestamp);
            e.Property(x => x.ReceivedAt).HasColumnType("timestamp").HasConversion(UtcToTimestamp);

            e.Ignore(x => x.WasTaken);

            // A signal sent twice is one signal: a unique key on the id is what says so, whoever asks first (the trades' foreign key,
            // below, points at it as an alternate key, which is that unique constraint).
            e.HasAlternateKey(x => x.SignalId);
            e.HasIndex(x => x.ReceivedAt);
        });

        builder.Entity<PaperTrade>(e =>
        {
            e.ToTable("paper_trades", "signals", t =>
            {
                t.HasCheckConstraint("CK_paper_trades_leverage", "\"Leverage\" >= 1");
                // Open means no exit yet; anything else has both. A trade cannot end without saying when and where.
                t.HasCheckConstraint("CK_paper_trades_ended_state",
                    "(\"Status\" = 1 AND \"ClosedAt\" IS NULL AND \"ExitPrice\" IS NULL) OR (\"Status\" <> 1 AND \"ClosedAt\" IS NOT NULL AND \"ExitPrice\" IS NOT NULL)");
                t.HasCheckConstraint("CK_paper_trades_message", "(\"TelegramChatId\" IS NULL) = (\"TelegramMessageId\" IS NULL)");
            });

            e.Property(x => x.SignalId).HasMaxLength(96);
            e.Property(x => x.Strategy).HasMaxLength(32);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Exchange).HasConversion<int>().HasDefaultValue(ExchangeEnum.Binance).HasSentinel(ExchangeEnum.Binance);
            e.Property(x => x.Side).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Leverage).HasPrecision(18, 4);
            e.Property(x => x.TakeProfit).HasPrecision(28, 8);
            e.Property(x => x.StopLoss).HasPrecision(28, 8);
            e.Property(x => x.SignalPrice).HasPrecision(28, 8);
            e.Property(x => x.EntryPrice).HasPrecision(28, 8);
            e.Property(x => x.ExitPrice).HasPrecision(28, 8);
            e.Property(x => x.BestPrice).HasPrecision(28, 8);
            e.Property(x => x.WorstPrice).HasPrecision(28, 8);
            e.Property(x => x.GrossResult).HasPrecision(18, 8);
            e.Property(x => x.FeesPaid).HasPrecision(18, 8);
            e.Property(x => x.FundingPaid).HasPrecision(18, 8);
            e.Property(x => x.NetResult).HasPrecision(18, 8);
            e.Property(x => x.RMultiple).HasPrecision(18, 4);

            e.Property(x => x.OpenedAt).HasColumnType("timestamp").HasConversion(UtcToTimestamp);
            e.Property(x => x.ExpiresAt).HasColumnType("timestamp").HasConversion(UtcToTimestamp);
            e.Property(x => x.ClosedAt).HasColumnType("timestamp").HasConversion(UtcToTimestamp);
            e.Property(x => x.LastCheckedAt).HasColumnType("timestamp").HasConversion(UtcToTimestamp);

            // A trade belongs to the signal that made it, and one signal makes at most one trade.
            e.HasOne<ReceivedSignal>().WithMany().HasForeignKey(x => x.SignalId).HasPrincipalKey(x => x.SignalId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SignalId).IsUnique();

            // What is asked all day long: the open trades, and how many a strategy has on a symbol.
            e.HasIndex(x => new { x.Exchange, x.Strategy, x.Symbol }).HasFilter("\"Status\" = 1");
            e.HasIndex(x => x.OpenedAt);
        });

        builder.Entity<SignalEvent>(e =>
        {
            e.ToTable("events", "signals");

            e.Property(x => x.SignalId).HasMaxLength(96);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Detail).HasMaxLength(500);
            e.Property(x => x.At).HasColumnType("timestamp").HasConversion(UtcToTimestamp);

            e.HasIndex(x => new { x.SignalId, x.At });
        });
    }
}
