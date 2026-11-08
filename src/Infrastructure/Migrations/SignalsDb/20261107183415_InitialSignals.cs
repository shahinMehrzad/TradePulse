using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations.SignalsDb
{
    /// <inheritdoc />
    public partial class InitialSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "signals");

            migrationBuilder.CreateTable(
                name: "events",
                schema: "signals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SignalId = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    At = table.Column<DateTime>(type: "timestamp", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "signals",
                schema: "signals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SignalId = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Strategy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    Leverage = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    TakeProfit = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    StopLoss = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    SignalPrice = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    TimeFrame = table.Column<int>(type: "integer", nullable: false),
                    CandleOpenTime = table.Column<DateTime>(type: "timestamp", nullable: false),
                    CandleCloseTime = table.Column<DateTime>(type: "timestamp", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp", nullable: false),
                    MaxDurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp", nullable: false),
                    Rejection = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signals", x => x.Id);
                    table.UniqueConstraint("AK_signals_SignalId", x => x.SignalId);
                    table.CheckConstraint("CK_signals_leverage", "\"Leverage\" >= 1");
                });

            migrationBuilder.CreateTable(
                name: "paper_trades",
                schema: "signals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SignalId = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Strategy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    Leverage = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    TakeProfit = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    StopLoss = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    SignalPrice = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "timestamp", nullable: false),
                    OpenTradesAtEntry = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExitPrice = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp", nullable: true),
                    BestPrice = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    WorstPrice = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    TelegramChatId = table.Column<long>(type: "bigint", nullable: true),
                    TelegramMessageId = table.Column<int>(type: "integer", nullable: true),
                    GrossResult = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    FeesPaid = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    FundingPaid = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    NetResult = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    RMultiple = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_trades", x => x.Id);
                    table.CheckConstraint("CK_paper_trades_ended_state", "(\"Status\" = 1 AND \"ClosedAt\" IS NULL AND \"ExitPrice\" IS NULL) OR (\"Status\" <> 1 AND \"ClosedAt\" IS NOT NULL AND \"ExitPrice\" IS NOT NULL)");
                    table.CheckConstraint("CK_paper_trades_leverage", "\"Leverage\" >= 1");
                    table.CheckConstraint("CK_paper_trades_message", "(\"TelegramChatId\" IS NULL) = (\"TelegramMessageId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_paper_trades_signals_SignalId",
                        column: x => x.SignalId,
                        principalSchema: "signals",
                        principalTable: "signals",
                        principalColumn: "SignalId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_events_SignalId_At",
                schema: "signals",
                table: "events",
                columns: new[] { "SignalId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_paper_trades_OpenedAt",
                schema: "signals",
                table: "paper_trades",
                column: "OpenedAt");

            migrationBuilder.CreateIndex(
                name: "IX_paper_trades_SignalId",
                schema: "signals",
                table: "paper_trades",
                column: "SignalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_paper_trades_Strategy_Symbol",
                schema: "signals",
                table: "paper_trades",
                columns: new[] { "Strategy", "Symbol" },
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_signals_ReceivedAt",
                schema: "signals",
                table: "signals",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "events",
                schema: "signals");

            migrationBuilder.DropTable(
                name: "paper_trades",
                schema: "signals");

            migrationBuilder.DropTable(
                name: "signals",
                schema: "signals");
        }
    }
}
