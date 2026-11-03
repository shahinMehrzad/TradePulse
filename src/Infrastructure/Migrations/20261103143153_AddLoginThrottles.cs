using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginThrottles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "login_throttles",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Failures = table.Column<int>(type: "integer", nullable: false),
                    FirstFailureAt = table.Column<DateTime>(type: "timestamp", nullable: true),
                    Rounds = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "timestamp", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login_throttles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_login_throttles_UserId_Kind_Address",
                schema: "auth",
                table: "login_throttles",
                columns: new[] { "UserId", "Kind", "Address" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "login_throttles",
                schema: "auth");
        }
    }
}
