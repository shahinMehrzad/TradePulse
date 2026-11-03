using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations.ConfigDb
{
    /// <summary>
    /// A read-only copy of the symbols enabled for trading, for the API and the watch. The truth is in the key vault's own database
    /// (<c>vault.trading_symbols</c>); the trading service publishes the enabled symbols here. Nothing is decided from this table: a
    /// changed row can alter what the watch follows, never what may be traded.
    /// </summary>
    [DbContext(typeof(ConfigDbContext))]
    [Migration("20261103100000_TradedSymbolsCopy")]
    public partial class TradedSymbolsCopy : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS config.traded_symbols (
                    symbol     text PRIMARY KEY,
                    changed_at timestamp NOT NULL
                );
                """);

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DROP TABLE IF EXISTS config.traded_symbols;");
    }
}
