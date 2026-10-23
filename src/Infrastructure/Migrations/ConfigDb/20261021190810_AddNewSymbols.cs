using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Infrastructure.Migrations.ConfigDb
{
    /// <inheritdoc />
    public partial class AddNewSymbols : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "config",
                table: "symbols",
                columns: new[] { "Id", "Description", "Name" },
                values: new object[,]
                {
                    { 9, null, "DOGEUSDT" },
                    { 10, null, "LINKUSDT" },
                    { 11, null, "LTCUSDT" },
                    { 12, null, "AVAXUSDT" },
                    { 13, null, "DOTUSDT" },
                    { 14, null, "TRXUSDT" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 9);

            migrationBuilder.DeleteData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 10);

            migrationBuilder.DeleteData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 11);

            migrationBuilder.DeleteData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 12);

            migrationBuilder.DeleteData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 13);

            migrationBuilder.DeleteData(
                schema: "config",
                table: "symbols",
                keyColumn: "Id",
                keyValue: 14);
        }
    }
}
