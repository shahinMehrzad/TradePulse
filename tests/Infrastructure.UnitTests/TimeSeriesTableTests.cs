using Infrastructure.Repositories.TimeSeries;

namespace Infrastructure.UnitTests;

/// <summary>
/// The repository trusts each table definition to be self-consistent: the column list drives COPY, the
/// time column drives "resume from newest row", and the CREATE statement defines what actually exists.
/// A typo in any of them only shows up at runtime against Postgres, so check the relationships here.
/// </summary>
public class TimeSeriesTableTests
{
    private const string Table = "binance_futures_btcusdt_test";

    public static TheoryData<string, ITimeSeriesTableInfo> Tables() => new()
    {
        { "funding rate", new TableInfo<Domain.Entities.FundingRate>(new FundingRateTable()) },
        { "open interest", new TableInfo<Domain.Entities.OpenInterest>(new OpenInterestTable()) },
        { "liquidation", new TableInfo<Domain.Entities.Liquidation>(new LiquidationTable()) },
    };

    [Theory]
    [MemberData(nameof(Tables))]
    public void EveryColumnIsDefinedInTheCreateStatement(string name, ITimeSeriesTableInfo table)
    {
        var sql = table.CreateTableSql(Table);

        foreach (var column in table.Columns)
            Assert.True(sql.Contains($"{column} ", StringComparison.Ordinal), $"{name}: column '{column}' missing from CREATE TABLE");
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void TimeColumnIsOneOfTheColumns(string name, ITimeSeriesTableInfo table)
    {
        Assert.True(table.Columns.Contains(table.TimeColumn), $"{name}: time column is not in the column list");
        Assert.Equal(table.TimeColumn, table.Columns[0]);
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void ColumnNamesAreUnique(string name, ITimeSeriesTableInfo table)
    {
        Assert.True(table.Columns.Distinct().Count() == table.Columns.Count, $"{name}: duplicate column name");
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void TimeColumnsUseTimestampWithoutTimeZone(string name, ITimeSeriesTableInfo table)
    {
        var sql = table.CreateTableSql(Table);

        Assert.True(sql.Contains($"{table.TimeColumn} timestamp NOT NULL"), $"{name}: time column must be timestamp NOT NULL");
        Assert.DoesNotContain("timestamptz", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("with time zone", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void CreateStatementIsIdempotent(string name, ITimeSeriesTableInfo table)
    {
        // The seeder runs on every start.
        var sql = table.CreateTableSql(Table);

        Assert.True(sql.Contains("CREATE TABLE IF NOT EXISTS " + Table), $"{name}: not idempotent");
        Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FundingRateAndOpenInterestAreKeyedByTime()
    {
        // The primary key is what makes re-fetching a range a no-op (ON CONFLICT DO NOTHING).
        Assert.Contains($"PRIMARY KEY ({new FundingRateTable().TimeColumn})", new FundingRateTable().CreateTableSql(Table));
        Assert.Contains($"PRIMARY KEY ({new OpenInterestTable().TimeColumn})", new OpenInterestTable().CreateTableSql(Table));
    }

    [Fact]
    public void LiquidationsAreNotKeyedButAreIndexedByTime()
    {
        var sql = new LiquidationTable().CreateTableSql(Table);

        Assert.DoesNotContain("PRIMARY KEY", sql);
        Assert.Contains($"CREATE INDEX IF NOT EXISTS ix_{Table}_event_time ON {Table} (event_time)", sql);
    }

    [Fact]
    public void TimestampsRoundTripThroughUnspecifiedKind()
    {
        var utc = new DateTime(2024, 5, 6, 7, 8, 9, 10, DateTimeKind.Utc);

        var stored = Timestamps.ToTimestamp(utc);
        var loaded = Timestamps.FromTimestamp(stored);

        Assert.Equal(DateTimeKind.Unspecified, stored.Kind);
        Assert.Equal(utc.Ticks, stored.Ticks);
        Assert.Equal(DateTimeKind.Utc, loaded.Kind);
        Assert.Equal(utc, loaded);
    }
}

/// <summary>Non-generic view over <see cref="ITimeSeriesTable{T}"/> so one theory can cover every table.</summary>
public interface ITimeSeriesTableInfo
{
    string TimeColumn { get; }
    IReadOnlyList<string> Columns { get; }
    string CreateTableSql(string tableName);
}

public sealed class TableInfo<T>(ITimeSeriesTable<T> table) : ITimeSeriesTableInfo
{
    public string TimeColumn => table.TimeColumn;
    public IReadOnlyList<string> Columns => table.Columns;
    public string CreateTableSql(string tableName) => table.CreateTableSql(tableName);
}
