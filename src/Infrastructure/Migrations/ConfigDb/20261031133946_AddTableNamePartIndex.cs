using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations.ConfigDb
{
    /// <inheritdoc />
    public partial class AddTableNamePartIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The collector's tables are named after the exchange's name with - _ . / dropped and lower-cased (TableNameRegistry), so
            // BTC-USDT and BTCUSDT on one exchange market would write to the same tables. The plain unique index cannot see that;
            // this one can, and it stops two admins listing both at the same time. EF has no model for an expression index, so it
            // is plain SQL here. It fails to build if two listings of one exchange market already share their table names.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_exchange_symbols_table_name_part"
                ON config.exchange_symbols ("ExchangeId", "MarketId", lower(regexp_replace("ExchangeSymbol", '[-_./]', '', 'g')));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX config."IX_exchange_symbols_table_name_part";""");
        }
    }
}
