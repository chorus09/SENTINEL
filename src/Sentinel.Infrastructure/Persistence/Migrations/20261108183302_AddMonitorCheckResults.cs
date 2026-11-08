using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitorCheckResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "monitor_check_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MonitorId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurationMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    HttpStatusCode = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monitor_check_results", x => x.Id);
                    table.CheckConstraint("CK_monitor_check_results_Duration", "\"DurationMilliseconds\" >= 0");
                    table.CheckConstraint("CK_monitor_check_results_HttpStatus", "\"HttpStatusCode\" IS NULL OR \"HttpStatusCode\" BETWEEN 100 AND 599");
                    table.ForeignKey(
                        name: "FK_monitor_check_results_monitors_MonitorId",
                        column: x => x.MonitorId,
                        principalTable: "monitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_monitor_check_results_Monitor_Recent",
                table: "monitor_check_results",
                columns: new[] { "MonitorId", "CheckedAt", "Id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "monitor_check_results");
        }
    }
}
