using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations.SignalsDb
{
    /// <inheritdoc />
    public partial class AddExchangeToSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_paper_trades_Strategy_Symbol",
                schema: "signals",
                table: "paper_trades");

            migrationBuilder.AddColumn<int>(
                name: "Exchange",
                schema: "signals",
                table: "signals",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "Exchange",
                schema: "signals",
                table: "paper_trades",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_paper_trades_Exchange_Strategy_Symbol",
                schema: "signals",
                table: "paper_trades",
                columns: new[] { "Exchange", "Strategy", "Symbol" },
                filter: "\"Status\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_paper_trades_Exchange_Strategy_Symbol",
                schema: "signals",
                table: "paper_trades");

            migrationBuilder.DropColumn(
                name: "Exchange",
                schema: "signals",
                table: "signals");

            migrationBuilder.DropColumn(
                name: "Exchange",
                schema: "signals",
                table: "paper_trades");

            migrationBuilder.CreateIndex(
                name: "IX_paper_trades_Strategy_Symbol",
                schema: "signals",
                table: "paper_trades",
                columns: new[] { "Strategy", "Symbol" },
                filter: "\"Status\" = 1");
        }
    }
}
