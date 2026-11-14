using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitorIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveHealthyCount",
                table: "monitors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveUnhealthyCount",
                table: "monitors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "monitor_incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MonitorId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monitor_incidents", x => x.Id);
                    table.CheckConstraint("CK_monitor_incidents_ResolvedAt", "\"ResolvedAt\" IS NULL OR \"ResolvedAt\" >= \"OpenedAt\"");
                    table.ForeignKey(
                        name: "FK_monitor_incidents_monitors_MonitorId",
                        column: x => x.MonitorId,
                        principalTable: "monitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitors_ConsecutiveHealthyCount",
                table: "monitors",
                sql: "\"ConsecutiveHealthyCount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitors_ConsecutiveUnhealthyCount",
                table: "monitors",
                sql: "\"ConsecutiveUnhealthyCount\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_monitor_incidents_Monitor_Open",
                table: "monitor_incidents",
                column: "MonitorId",
                unique: true,
                filter: "\"ResolvedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_monitor_incidents_Monitor_Recent",
                table: "monitor_incidents",
                columns: new[] { "MonitorId", "OpenedAt", "Id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "monitor_incidents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_monitors_ConsecutiveHealthyCount",
                table: "monitors");

            migrationBuilder.DropCheckConstraint(
                name: "CK_monitors_ConsecutiveUnhealthyCount",
                table: "monitors");

            migrationBuilder.DropColumn(
                name: "ConsecutiveHealthyCount",
                table: "monitors");

            migrationBuilder.DropColumn(
                name: "ConsecutiveUnhealthyCount",
                table: "monitors");
        }
    }
}
