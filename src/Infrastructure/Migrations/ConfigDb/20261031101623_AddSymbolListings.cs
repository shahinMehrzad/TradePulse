using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Infrastructure.Migrations.ConfigDb
{
    /// <inheritdoc />
    public partial class AddSymbolListings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "Id",
                schema: "config",
                table: "symbols",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<int>(
                name: "SymbolListingId",
                schema: "config",
                table: "symbol_data_configurations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SymbolListingId",
                schema: "config",
                table: "candle_worker_configurations",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "exchange_symbols",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExchangeId = table.Column<int>(type: "integer", nullable: false),
                    MarketId = table.Column<int>(type: "integer", nullable: false),
                    SymbolId = table.Column<int>(type: "integer", nullable: false),
                    ExchangeSymbol = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exchange_symbols", x => x.Id);
                    table.ForeignKey(
                        name: "FK_exchange_symbols_exchanges_ExchangeId",
                        column: x => x.ExchangeId,
                        principalSchema: "config",
                        principalTable: "exchanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_exchange_symbols_markets_MarketId",
                        column: x => x.MarketId,
                        principalSchema: "config",
                        principalTable: "markets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_exchange_symbols_symbols_SymbolId",
                        column: x => x.SymbolId,
                        principalSchema: "config",
                        principalTable: "symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "config",
                table: "exchange_symbols",
                columns: new[] { "Id", "ExchangeId", "ExchangeSymbol", "MarketId", "SymbolId" },
                values: new object[,]
                {
                    { 1, 1, "BTCUSDT", 2, 1 },
                    { 2, 1, "XAUUSDT", 2, 2 },
                    { 3, 1, "ETHUSDT", 2, 3 },
                    { 4, 1, "SOLUSDT", 2, 4 },
                    { 5, 1, "XRPUSDT", 2, 5 },
                    { 6, 1, "BNBUSDT", 2, 6 },
                    { 7, 1, "SUIUSDT", 2, 7 },
                    { 8, 1, "ADAUSDT", 2, 8 },
                    { 9, 1, "DOGEUSDT", 2, 9 },
                    { 10, 1, "LINKUSDT", 2, 10 },
                    { 11, 1, "LTCUSDT", 2, 11 },
                    { 12, 1, "AVAXUSDT", 2, 12 },
                    { 13, 1, "DOTUSDT", 2, 13 },
                    { 14, 1, "TRXUSDT", 2, 14 }
                });

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 1,
                column: "Name",
                value: "BTC/USDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 2,
                column: "Name",
                value: "XAU/USDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 3,
                column: "Name",
                value: "ETH/USDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 4,
                column: "Name",
                value: "SOL/USDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 5,
                column: "Name",
                value: "XRP/USDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 6,
                column: "Name",
                value: "BNB/USDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 7,
                column: "Name",
                value: "SUI/USDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 8,
                column: "Name",
                value: "ADA/USDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 9,
                column: "Name",
                value: "DOGE/USDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 10,
                column: "Name",
                value: "LINK/USDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 11,
                column: "Name",
                value: "LTC/USDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 12,
                column: "Name",
                value: "AVAX/USDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 13,
                column: "Name",
                value: "DOT/USDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 14,
                column: "Name",
                value: "TRX/USDT");

            // The rows above were inserted with explicit ids, which does not move an identity sequence: set both
            // sequences past them, or the first symbol an admin adds would collide with id 1.
            migrationBuilder.Sql("""
                SELECT setval(pg_get_serial_sequence('config.symbols', 'Id'), COALESCE(MAX("Id"), 0) + 1, false) FROM config.symbols;
                SELECT setval(pg_get_serial_sequence('config.exchange_symbols', 'Id'), COALESCE(MAX("Id"), 0) + 1, false) FROM config.exchange_symbols;
                """);

            // Existing configurations keep working: list whatever exchange, market and symbol they already use (the name is
            // the old enum name, which is what the exchange API and the table names have always used), then point them at it.
            migrationBuilder.Sql("""
                INSERT INTO config.exchange_symbols ("ExchangeId", "MarketId", "SymbolId", "ExchangeSymbol")
                SELECT c."ExchangeId", c."MarketId", c."SymbolId", replace(s."Name", '/', '')
                FROM (SELECT "ExchangeId", "MarketId", "SymbolId" FROM config.candle_worker_configurations
                      UNION
                      SELECT "ExchangeId", "MarketId", "SymbolId" FROM config.symbol_data_configurations) c
                JOIN config.symbols s ON s."Id" = c."SymbolId"
                WHERE NOT EXISTS (SELECT 1 FROM config.exchange_symbols l
                                  WHERE l."ExchangeId" = c."ExchangeId" AND l."MarketId" = c."MarketId" AND l."SymbolId" = c."SymbolId");

                UPDATE config.candle_worker_configurations c SET "SymbolListingId" = l."Id"
                FROM config.exchange_symbols l
                WHERE l."ExchangeId" = c."ExchangeId" AND l."MarketId" = c."MarketId" AND l."SymbolId" = c."SymbolId";

                UPDATE config.symbol_data_configurations c SET "SymbolListingId" = l."Id"
                FROM config.exchange_symbols l
                WHERE l."ExchangeId" = c."ExchangeId" AND l."MarketId" = c."MarketId" AND l."SymbolId" = c."SymbolId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_symbols_Name",
                schema: "config",
                table: "symbols",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_symbol_data_configurations_SymbolListingId",
                schema: "config",
                table: "symbol_data_configurations",
                column: "SymbolListingId");

            migrationBuilder.CreateIndex(
                name: "IX_candle_worker_configurations_SymbolListingId",
                schema: "config",
                table: "candle_worker_configurations",
                column: "SymbolListingId");

            migrationBuilder.CreateIndex(
                name: "IX_exchange_symbols_ExchangeId_MarketId_ExchangeSymbol",
                schema: "config",
                table: "exchange_symbols",
                columns: new[] { "ExchangeId", "MarketId", "ExchangeSymbol" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_exchange_symbols_ExchangeId_MarketId_SymbolId",
                schema: "config",
                table: "exchange_symbols",
                columns: new[] { "ExchangeId", "MarketId", "SymbolId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_exchange_symbols_MarketId",
                schema: "config",
                table: "exchange_symbols",
                column: "MarketId");

            migrationBuilder.CreateIndex(
                name: "IX_exchange_symbols_SymbolId",
                schema: "config",
                table: "exchange_symbols",
                column: "SymbolId");

            migrationBuilder.AddForeignKey(
                name: "FK_candle_worker_configurations_exchange_symbols_SymbolListing~",
                schema: "config",
                table: "candle_worker_configurations",
                column: "SymbolListingId",
                principalSchema: "config",
                principalTable: "exchange_symbols",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_symbol_data_configurations_exchange_symbols_SymbolListingId",
                schema: "config",
                table: "symbol_data_configurations",
                column: "SymbolListingId",
                principalSchema: "config",
                principalTable: "exchange_symbols",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_candle_worker_configurations_exchange_symbols_SymbolListing~",
                schema: "config",
                table: "candle_worker_configurations");

            migrationBuilder.DropForeignKey(
                name: "FK_symbol_data_configurations_exchange_symbols_SymbolListingId",
                schema: "config",
                table: "symbol_data_configurations");

            migrationBuilder.DropTable(
                name: "exchange_symbols",
                schema: "config");

            migrationBuilder.DropIndex(
                name: "IX_symbols_Name",
                schema: "config",
                table: "symbols");

            migrationBuilder.DropIndex(
                name: "IX_symbol_data_configurations_SymbolListingId",
                schema: "config",
                table: "symbol_data_configurations");

            migrationBuilder.DropIndex(
                name: "IX_candle_worker_configurations_SymbolListingId",
                schema: "config",
                table: "candle_worker_configurations");

            migrationBuilder.DropColumn(
                name: "SymbolListingId",
                schema: "config",
                table: "symbol_data_configurations");

            migrationBuilder.DropColumn(
                name: "SymbolListingId",
                schema: "config",
                table: "candle_worker_configurations");

            migrationBuilder.AlterColumn<int>(
                name: "Id",
                schema: "config",
                table: "symbols",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer")
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 1,
                column: "Name",
                value: "BTCUSDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 2,
                column: "Name",
                value: "XAUUSDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 3,
                column: "Name",
                value: "ETHUSDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 4,
                column: "Name",
                value: "SOLUSDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 5,
                column: "Name",
                value: "XRPUSDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 6,
                column: "Name",
                value: "BNBUSDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 7,
                column: "Name",
                value: "SUIUSDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 8,
                column: "Name",
                value: "ADAUSDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 9,
                column: "Name",
                value: "DOGEUSDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 10,
                column: "Name",
                value: "LINKUSDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 11,
                column: "Name",
                value: "LTCUSDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 12,
                column: "Name",
                value: "AVAXUSDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 13,
                column: "Name",
                value: "DOTUSDT");

            migrationBuilder.UpdateData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 14,
                column: "Name",
                value: "TRXUSDT");
        }
    }
}
