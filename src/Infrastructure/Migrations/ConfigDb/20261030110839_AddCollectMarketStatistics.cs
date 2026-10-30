using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations.ConfigDb
{
    /// <inheritdoc />
    public partial class AddCollectMarketStatistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CollectMarketStatistics",
                schema: "config",
                table: "candle_worker_configurations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CollectMarketStatistics",
                schema: "config",
                table: "candle_worker_configurations");
        }
    }
}
