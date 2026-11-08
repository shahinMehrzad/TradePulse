using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations.SignalsDb
{
    /// <inheritdoc />
    public partial class AddEndAnnouncedAtToPaperTrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EndAnnouncedAt",
                schema: "signals",
                table: "paper_trades",
                type: "timestamp",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndAnnouncedAt",
                schema: "signals",
                table: "paper_trades");
        }
    }
}
