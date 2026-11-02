using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations.ResearchDb
{
    /// <inheritdoc />
    public partial class ShockAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shock_alerts",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SymbolListingId = table.Column<int>(type: "integer", nullable: false),
                    TriggeredAt = table.Column<DateTime>(type: "timestamp", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    WindowSeconds = table.Column<int>(type: "integer", nullable: false),
                    Move = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    ZScore = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    StartPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    EndPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Volume = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    FlaggedSeconds = table.Column<int>(type: "integer", nullable: false),
                    RuleVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp", nullable: false, defaultValueSql: "now() at time zone 'utc'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shock_alerts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shock_alerts_SymbolListingId_RuleVersion_TriggeredAt",
                schema: "research",
                table: "shock_alerts",
                columns: new[] { "SymbolListingId", "RuleVersion", "TriggeredAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shock_alerts_TriggeredAt",
                schema: "research",
                table: "shock_alerts",
                column: "TriggeredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shock_alerts",
                schema: "research");
        }
    }
}
