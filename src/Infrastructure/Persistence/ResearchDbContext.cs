using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.Persistence;

/// <summary>What the analysis of the stored market data produces (events found in the candles and what the price did after them), apart from the data itself and from the settings.</summary>
public class ResearchDbContext : DbContext
{
    public ResearchDbContext(DbContextOptions<ResearchDbContext> options) : base(options)
    {
    }

    private static readonly ValueConverter<DateTime, DateTime> UtcToTimestamp = new(
        value => DateTime.SpecifyKind(value, DateTimeKind.Unspecified),
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public DbSet<MarketEvent> MarketEvents => Set<MarketEvent>();
    public DbSet<EventReaction> EventReactions => Set<EventReaction>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema("research");

        builder.Entity<MarketEvent>(e =>
        {
            e.ToTable("market_events", "research");

            // The listing lives in the config schema (possibly another database): its id is kept, with no foreign key to it, like the audit log does.
            e.Ignore(x => x.SymbolListing);

            // Time columns are timestamp (no zone), always UTC. Npgsql refuses Kind=Utc for them, so the kind is dropped on the way in
            // (values read from candles are Utc) and put back on the way out; the same applies to the values a query is given.
            e.Property(x => x.StartedAt).HasColumnType("timestamp").HasConversion(UtcToTimestamp);
            e.Property(x => x.CreateDate).HasColumnType("timestamp").HasConversion(UtcToTimestamp).HasDefaultValueSql("now() at time zone 'utc'");

            e.Property(x => x.Direction).HasConversion<int>();
            e.Property(x => x.FiveMinuteReturn).HasPrecision(18, 8);
            e.Property(x => x.ZScore).HasPrecision(18, 4);
            e.Property(x => x.VolumeRatio).HasPrecision(18, 4);
            e.Property(x => x.DetectorVersion).HasMaxLength(32);

            // Running the detector again over the same stretch must not add the same event twice.
            e.HasIndex(x => new { x.SymbolListingId, x.DetectorVersion, x.StartedAt }).IsUnique();
            e.HasIndex(x => x.StartedAt);
        });

        builder.Entity<EventReaction>(e =>
        {
            e.ToTable("event_reactions", "research");

            e.Property(x => x.Return).HasPrecision(18, 8);
            e.Property(x => x.MaxRise).HasPrecision(18, 8);
            e.Property(x => x.MaxFall).HasPrecision(18, 8);

            // A reaction means nothing without its event.
            e.HasOne(x => x.MarketEvent).WithMany(x => x.Reactions).HasForeignKey(x => x.MarketEventId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.MarketEventId, x.HorizonMinutes }).IsUnique();
        });
    }
}
