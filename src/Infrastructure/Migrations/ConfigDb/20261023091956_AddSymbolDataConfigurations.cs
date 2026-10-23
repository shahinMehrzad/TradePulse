using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations.ConfigDb
{
    /// <inheritdoc />
    public partial class AddSymbolDataConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "symbol_data_configurations",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExchangeId = table.Column<int>(type: "integer", nullable: false),
                    MarketId = table.Column<int>(type: "integer", nullable: false),
                    SymbolId = table.Column<int>(type: "integer", nullable: false),
                    CollectFundingRate = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    FundingStartDate = table.Column<DateTime>(type: "timestamp", nullable: true),
                    CollectLiquidation = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp", nullable: false, defaultValueSql: "now() at time zone 'utc'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_symbol_data_configurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_symbol_data_configurations_exchanges_ExchangeId",
                        column: x => x.ExchangeId,
                        principalSchema: "config",
                        principalTable: "exchanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_symbol_data_configurations_markets_MarketId",
                        column: x => x.MarketId,
                        principalSchema: "config",
                        principalTable: "markets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_symbol_data_configurations_symbols_SymbolId",
                        column: x => x.SymbolId,
                        principalSchema: "config",
                        principalTable: "symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "symbol_data_configurations",
                schema: "config");
        }
    }
}
