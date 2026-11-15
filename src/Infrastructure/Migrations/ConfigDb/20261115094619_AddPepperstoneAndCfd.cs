using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations.ConfigDb
{
    /// <inheritdoc />
    public partial class AddPepperstoneAndCfd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "config",
                table: "exchanges",
                columns: new[] { "Id", "Name" },
                values: new object[] { 2, "Pepperstone" });

            migrationBuilder.InsertData(
                schema: "config",
                table: "markets",
                columns: new[] { "Id", "Name" },
                values: new object[] { 3, "Cfd" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "config",
                table: "exchanges",
                keyColumn: "Id",
                keyValue: 2);

            migrationBuilder.DeleteData(
                schema: "config",
                table: "markets",
                keyColumn: "Id",
                keyValue: 3);
        }
    }
}
