using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations.ConfigDb
{
    /// <inheritdoc />
    public partial class AddCandleConfirmedGaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "candle_confirmed_gaps",
                schema: "config",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TableName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    FirstOpenTime = table.Column<DateTime>(type: "timestamp", nullable: false),
                    LastOpenTime = table.Column<DateTime>(type: "timestamp", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp", nullable: false, defaultValueSql: "now() at time zone 'utc'"),
                    Asks = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candle_confirmed_gaps", x => x.Id);
                    table.CheckConstraint("CK_candle_confirmed_gaps_range", "\"LastOpenTime\" >= \"FirstOpenTime\"");
                });

            migrationBuilder.CreateIndex(
                name: "IX_candle_confirmed_gaps_TableName_FirstOpenTime_LastOpenTime",
                schema: "config",
                table: "candle_confirmed_gaps",
                columns: new[] { "TableName", "FirstOpenTime", "LastOpenTime" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candle_confirmed_gaps",
                schema: "config");
        }
    }
}
