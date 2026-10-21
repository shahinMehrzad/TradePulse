using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Infrastructure.Migrations.ConfigDb
{
    /// <inheritdoc />
    public partial class InitialConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "config");

            migrationBuilder.CreateTable(
                name: "exchanges",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exchanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "markets",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_markets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "symbols",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_symbols", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "time_frames",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    IntervalToSeconds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_time_frames", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "candle_worker_configurations",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExchangeId = table.Column<int>(type: "integer", nullable: false),
                    MarketId = table.Column<int>(type: "integer", nullable: false),
                    SymbolId = table.Column<int>(type: "integer", nullable: false),
                    TimeFrameId = table.Column<int>(type: "integer", nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp", nullable: false, defaultValueSql: "now() at time zone 'utc'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candle_worker_configurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_candle_worker_configurations_exchanges_ExchangeId",
                        column: x => x.ExchangeId,
                        principalSchema: "config",
                        principalTable: "exchanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_candle_worker_configurations_markets_MarketId",
                        column: x => x.MarketId,
                        principalSchema: "config",
                        principalTable: "markets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_candle_worker_configurations_symbols_SymbolId",
                        column: x => x.SymbolId,
                        principalSchema: "config",
                        principalTable: "symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_candle_worker_configurations_time_frames_TimeFrameId",
                        column: x => x.TimeFrameId,
                        principalSchema: "config",
                        principalTable: "time_frames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "config",
                table: "exchanges",
                columns: new[] { "Id", "Name" },
                values: new object[] { 1, "Binance" });

            migrationBuilder.InsertData(
                schema: "config",
                table: "markets",
                columns: new[] { "Id", "Name" },
                values: new object[,]
                {
                    { 1, "Spot" },
                    { 2, "Futures" }
                });

            migrationBuilder.InsertData(
                schema: "config",
                table: "symbols",
                columns: new[] { "Id", "Description", "Name" },
                values: new object[,]
                {
                    { 1, null, "BTCUSDT" },
                    { 2, null, "XAUUSDT" },
                    { 3, null, "ETHUSDT" },
                    { 4, null, "SOLUSDT" },
                    { 5, null, "XRPUSDT" },
                    { 6, null, "BNBUSDT" },
                    { 7, null, "SUIUSDT" },
                    { 8, null, "ADAUSDT" }
                });

            migrationBuilder.InsertData(
                schema: "config",
                table: "time_frames",
                columns: new[] { "Id", "IntervalToSeconds", "Name" },
                values: new object[,]
                {
                    { 1, 1, "_1S" },
                    { 60, 60, "_1M" },
                    { 180, 180, "_3M" },
                    { 300, 300, "_5M" },
                    { 900, 900, "_15M" },
                    { 1800, 1800, "_30M" },
                    { 3600, 3600, "_1H" },
                    { 7200, 7200, "_2H" },
                    { 14400, 14400, "_4H" },
                    { 21600, 21600, "_6H" },
                    { 28800, 28800, "_8H" },
                    { 43200, 43200, "_12H" },
                    { 86400, 86400, "_1D" }
                });

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
                name: "IX_candle_worker_configurations_TimeFrameId",
                schema: "config",
                table: "candle_worker_configurations",
                column: "TimeFrameId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candle_worker_configurations",
                schema: "config");

            migrationBuilder.DropTable(
                name: "exchanges",
                schema: "config");

            migrationBuilder.DropTable(
                name: "markets",
                schema: "config");

            migrationBuilder.DropTable(
                name: "symbols",
                schema: "config");

            migrationBuilder.DropTable(
                name: "time_frames",
                schema: "config");
        }
    }
}
