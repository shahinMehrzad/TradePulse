using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations.ConfigDb
{
    /// <inheritdoc />
    public partial class MakeSignalAgeLimitOptional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_signal_strategy_configurations_age",
                schema: "config",
                table: "signal_strategy_configurations");

            migrationBuilder.AlterColumn<int>(
                name: "MaxSignalAgeSeconds",
                schema: "config",
                table: "signal_strategy_configurations",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddCheckConstraint(
                name: "CK_signal_strategy_configurations_age",
                schema: "config",
                table: "signal_strategy_configurations",
                sql: "\"MaxSignalAgeSeconds\" IS NULL OR \"MaxSignalAgeSeconds\" BETWEEN 1 AND 604800");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_signal_strategy_configurations_age",
                schema: "config",
                table: "signal_strategy_configurations");

            migrationBuilder.AlterColumn<int>(
                name: "MaxSignalAgeSeconds",
                schema: "config",
                table: "signal_strategy_configurations",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_signal_strategy_configurations_age",
                schema: "config",
                table: "signal_strategy_configurations",
                sql: "\"MaxSignalAgeSeconds\" BETWEEN 1 AND 604800");
        }
    }
}
