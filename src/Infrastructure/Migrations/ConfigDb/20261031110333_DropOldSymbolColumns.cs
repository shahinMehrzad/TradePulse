using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations.ConfigDb
{
    /// <inheritdoc />
    public partial class DropOldSymbolColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A row saved while only the old columns were filled (the previous migration was applied but the application
            // was not updated yet) gets its listing now, the same way that migration gave one to every older row.
            migrationBuilder.Sql("""
                INSERT INTO config.exchange_symbols ("ExchangeId", "MarketId", "SymbolId", "ExchangeSymbol")
                SELECT c."ExchangeId", c."MarketId", c."SymbolId", replace(s."Name", '/', '')
                FROM (SELECT "ExchangeId", "MarketId", "SymbolId" FROM config.candle_worker_configurations WHERE "SymbolListingId" IS NULL
                      UNION
                      SELECT "ExchangeId", "MarketId", "SymbolId" FROM config.symbol_data_configurations WHERE "SymbolListingId" IS NULL) c
                JOIN config.symbols s ON s."Id" = c."SymbolId"
                WHERE NOT EXISTS (SELECT 1 FROM config.exchange_symbols l
                                  WHERE l."ExchangeId" = c."ExchangeId" AND l."MarketId" = c."MarketId" AND l."SymbolId" = c."SymbolId");

                UPDATE config.candle_worker_configurations c SET "SymbolListingId" = l."Id"
                FROM config.exchange_symbols l
                WHERE c."SymbolListingId" IS NULL
                  AND l."ExchangeId" = c."ExchangeId" AND l."MarketId" = c."MarketId" AND l."SymbolId" = c."SymbolId";

                UPDATE config.symbol_data_configurations c SET "SymbolListingId" = l."Id"
                FROM config.exchange_symbols l
                WHERE c."SymbolListingId" IS NULL
                  AND l."ExchangeId" = c."ExchangeId" AND l."MarketId" = c."MarketId" AND l."SymbolId" = c."SymbolId";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_candle_worker_configurations_exchanges_ExchangeId",
                schema: "config",
                table: "candle_worker_configurations");

            migrationBuilder.DropForeignKey(
                name: "FK_candle_worker_configurations_markets_MarketId",
                schema: "config",
                table: "candle_worker_configurations");

            migrationBuilder.DropForeignKey(
                name: "FK_candle_worker_configurations_symbols_SymbolId",
                schema: "config",
                table: "candle_worker_configurations");

            migrationBuilder.DropForeignKey(
                name: "FK_symbol_data_configurations_exchanges_ExchangeId",
                schema: "config",
                table: "symbol_data_configurations");

            migrationBuilder.DropForeignKey(
                name: "FK_symbol_data_configurations_markets_MarketId",
                schema: "config",
                table: "symbol_data_configurations");

            migrationBuilder.DropForeignKey(
                name: "FK_symbol_data_configurations_symbols_SymbolId",
                schema: "config",
                table: "symbol_data_configurations");

            migrationBuilder.DropIndex(
                name: "IX_symbol_data_configurations_ExchangeId_MarketId_SymbolId",
                schema: "config",
                table: "symbol_data_configurations");

            migrationBuilder.DropIndex(
                name: "IX_symbol_data_configurations_MarketId",
                schema: "config",
                table: "symbol_data_configurations");

            migrationBuilder.DropIndex(
                name: "IX_symbol_data_configurations_SymbolId",
                schema: "config",
                table: "symbol_data_configurations");

            migrationBuilder.DropIndex(
                name: "IX_symbol_data_configurations_SymbolListingId",
                schema: "config",
                table: "symbol_data_configurations");

            migrationBuilder.DropIndex(
                name: "IX_candle_worker_configurations_ExchangeId_MarketId_SymbolId_T~",
                schema: "config",
                table: "candle_worker_configurations");

            migrationBuilder.DropIndex(
                name: "IX_candle_worker_configurations_MarketId",
                schema: "config",
                table: "candle_worker_configurations");

            migrationBuilder.DropIndex(
                name: "IX_candle_worker_configurations_SymbolId",
                schema: "config",
                table: "candle_worker_configurations");

            migrationBuilder.DropIndex(
                name: "IX_candle_worker_configurations_SymbolListingId",
                schema: "config",
                table: "candle_worker_configurations");

            migrationBuilder.DropColumn(
                name: "ExchangeId",
                schema: "config",
                table: "symbol_data_configurations");

            migrationBuilder.DropColumn(
                name: "MarketId",
                schema: "config",
                table: "symbol_data_configurations");

            migrationBuilder.DropColumn(
                name: "SymbolId",
                schema: "config",
                table: "symbol_data_configurations");

            migrationBuilder.DropColumn(
                name: "ExchangeId",
                schema: "config",
                table: "candle_worker_configurations");

            migrationBuilder.DropColumn(
                name: "MarketId",
                schema: "config",
                table: "candle_worker_configurations");

            migrationBuilder.DropColumn(
                name: "SymbolId",
                schema: "config",
                table: "candle_worker_configurations");

            migrationBuilder.AlterColumn<int>(
                name: "SymbolListingId",
                schema: "config",
                table: "symbol_data_configurations",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "SymbolListingId",
                schema: "config",
                table: "candle_worker_configurations",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_symbol_data_configurations_SymbolListingId",
                schema: "config",
                table: "symbol_data_configurations",
                column: "SymbolListingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_candle_worker_configurations_SymbolListingId_TimeFrameId",
                schema: "config",
                table: "candle_worker_configurations",
                columns: new[] { "SymbolListingId", "TimeFrameId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_symbol_data_configurations_SymbolListingId",
                schema: "config",
                table: "symbol_data_configurations");

            migrationBuilder.DropIndex(
                name: "IX_candle_worker_configurations_SymbolListingId_TimeFrameId",
                schema: "config",
                table: "candle_worker_configurations");

            migrationBuilder.AlterColumn<int>(
                name: "SymbolListingId",
                schema: "config",
                table: "symbol_data_configurations",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "ExchangeId",
                schema: "config",
                table: "symbol_data_configurations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MarketId",
                schema: "config",
                table: "symbol_data_configurations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SymbolId",
                schema: "config",
                table: "symbol_data_configurations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "SymbolListingId",
                schema: "config",
                table: "candle_worker_configurations",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "ExchangeId",
                schema: "config",
                table: "candle_worker_configurations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MarketId",
                schema: "config",
                table: "candle_worker_configurations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SymbolId",
                schema: "config",
                table: "candle_worker_configurations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE config.candle_worker_configurations c
                SET "ExchangeId" = l."ExchangeId", "MarketId" = l."MarketId", "SymbolId" = l."SymbolId"
                FROM config.exchange_symbols l WHERE l."Id" = c."SymbolListingId";

                UPDATE config.symbol_data_configurations c
                SET "ExchangeId" = l."ExchangeId", "MarketId" = l."MarketId", "SymbolId" = l."SymbolId"
                FROM config.exchange_symbols l WHERE l."Id" = c."SymbolListingId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_symbol_data_configurations_ExchangeId_MarketId_SymbolId",
                schema: "config",
                table: "symbol_data_configurations",
                columns: new[] { "ExchangeId", "MarketId", "SymbolId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_symbol_data_configurations_MarketId",
                schema: "config",
                table: "symbol_data_configurations",
                column: "MarketId");

            migrationBuilder.CreateIndex(
                name: "IX_symbol_data_configurations_SymbolId",
                schema: "config",
                table: "symbol_data_configurations",
                column: "SymbolId");

            migrationBuilder.CreateIndex(
                name: "IX_symbol_data_configurations_SymbolListingId",
                schema: "config",
                table: "symbol_data_configurations",
                column: "SymbolListingId");

            migrationBuilder.CreateIndex(
                name: "IX_candle_worker_configurations_ExchangeId_MarketId_SymbolId_T~",
                schema: "config",
                table: "candle_worker_configurations",
                columns: new[] { "ExchangeId", "MarketId", "SymbolId", "TimeFrameId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_candle_worker_configurations_MarketId",
                schema: "config",
                table: "candle_worker_configurations",
                column: "MarketId");

            migrationBuilder.CreateIndex(
                name: "IX_candle_worker_configurations_SymbolId",
                schema: "config",
                table: "candle_worker_configurations",
                column: "SymbolId");

            migrationBuilder.CreateIndex(
                name: "IX_candle_worker_configurations_SymbolListingId",
                schema: "config",
                table: "candle_worker_configurations",
                column: "SymbolListingId");

            migrationBuilder.AddForeignKey(
                name: "FK_candle_worker_configurations_exchanges_ExchangeId",
                schema: "config",
                table: "candle_worker_configurations",
                column: "ExchangeId",
                principalSchema: "config",
                principalTable: "exchanges",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_candle_worker_configurations_markets_MarketId",
                schema: "config",
                table: "candle_worker_configurations",
                column: "MarketId",
                principalSchema: "config",
                principalTable: "markets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_candle_worker_configurations_symbols_SymbolId",
                schema: "config",
                table: "candle_worker_configurations",
                column: "SymbolId",
                principalSchema: "config",
                principalTable: "symbols",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_symbol_data_configurations_exchanges_ExchangeId",
                schema: "config",
                table: "symbol_data_configurations",
                column: "ExchangeId",
                principalSchema: "config",
                principalTable: "exchanges",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_symbol_data_configurations_markets_MarketId",
                schema: "config",
                table: "symbol_data_configurations",
                column: "MarketId",
                principalSchema: "config",
                principalTable: "markets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_symbol_data_configurations_symbols_SymbolId",
                schema: "config",
                table: "symbol_data_configurations",
                column: "SymbolId",
                principalSchema: "config",
                principalTable: "symbols",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
