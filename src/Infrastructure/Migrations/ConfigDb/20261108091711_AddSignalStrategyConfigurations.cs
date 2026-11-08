using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations.ConfigDb
{
    /// <inheritdoc />
    public partial class AddSignalStrategyConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "signal_strategy_configurations",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Strategy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MaxSignalAgeSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxMoveOfStopDistance = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    MaxOpenPerSymbol = table.Column<int>(type: "integer", nullable: true),
                    MaxLeverage = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp", nullable: false, defaultValueSql: "now() at time zone 'utc'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signal_strategy_configurations", x => x.Id);
                    table.CheckConstraint("CK_signal_strategy_configurations_age", "\"MaxSignalAgeSeconds\" BETWEEN 1 AND 604800");
                    table.CheckConstraint("CK_signal_strategy_configurations_leverage", "\"MaxLeverage\" BETWEEN 1 AND 125");
                    table.CheckConstraint("CK_signal_strategy_configurations_move", "\"MaxMoveOfStopDistance\" > 0 AND \"MaxMoveOfStopDistance\" <= 1");
                    table.CheckConstraint("CK_signal_strategy_configurations_open", "\"MaxOpenPerSymbol\" IS NULL OR \"MaxOpenPerSymbol\" >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_signal_strategy_configurations_Strategy",
                schema: "config",
                table: "signal_strategy_configurations",
                column: "Strategy",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "signal_strategy_configurations",
                schema: "config");
        }
    }
}
