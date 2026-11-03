using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations.ResearchDb
{
    /// <inheritdoc />
    public partial class InitialResearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "research");

            migrationBuilder.CreateTable(
                name: "market_events",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SymbolListingId = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    FiveMinuteReturn = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    ZScore = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    VolumeRatio = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    FlaggedMinutes = table.Column<int>(type: "integer", nullable: false),
                    DetectorVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp", nullable: false, defaultValueSql: "now() at time zone 'utc'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "event_reactions",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MarketEventId = table.Column<int>(type: "integer", nullable: false),
                    HorizonMinutes = table.Column<int>(type: "integer", nullable: false),
                    Return = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MaxRise = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    MaxFall = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_reactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_event_reactions_market_events_MarketEventId",
                        column: x => x.MarketEventId,
                        principalSchema: "research",
                        principalTable: "market_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_event_reactions_MarketEventId_HorizonMinutes",
                schema: "research",
                table: "event_reactions",
                columns: new[] { "MarketEventId", "HorizonMinutes" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_market_events_StartedAt",
                schema: "research",
                table: "market_events",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_market_events_SymbolListingId_DetectorVersion_StartedAt",
                schema: "research",
                table: "market_events",
                columns: new[] { "SymbolListingId", "DetectorVersion", "StartedAt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_reactions",
                schema: "research");

            migrationBuilder.DropTable(
                name: "market_events",
                schema: "research");
        }
    }
}
