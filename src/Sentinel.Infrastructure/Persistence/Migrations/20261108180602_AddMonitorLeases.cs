using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitorLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ConfigVersion",
                table: "monitors",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                table: "monitors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseId",
                table: "monitors",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_monitors_Due",
                table: "monitors",
                column: "NextCheckAt",
                filter: "\"IsEnabled\" = TRUE");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitors_LeasePair",
                table: "monitors",
                sql: "(\"LeaseId\" IS NULL) = (\"LeaseExpiresAt\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM monitors
                               WHERE "LeaseId" IS NOT NULL AND "LeaseExpiresAt" > clock_timestamp()) THEN
                        RAISE EXCEPTION 'Stop monitoring workers before reverting monitor leases.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_monitors_Due",
                table: "monitors");

            migrationBuilder.DropCheckConstraint(
                name: "CK_monitors_LeasePair",
                table: "monitors");

            migrationBuilder.DropColumn(
                name: "ConfigVersion",
                table: "monitors");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "monitors");

            migrationBuilder.DropColumn(
                name: "LeaseId",
                table: "monitors");
        }
    }
}
