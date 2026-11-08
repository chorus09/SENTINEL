using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitorOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM monitors) THEN
                        RAISE EXCEPTION 'Monitor ownership migration requires an explicit decision for existing monitors.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_monitors_Slug",
                table: "monitors");

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "monitors",
                type: "uuid",
                nullable: false);

            migrationBuilder.CreateIndex(
                name: "IX_monitors_OwnerId_Slug",
                table: "monitors",
                columns: new[] { "OwnerId", "Slug" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_monitors_AspNetUsers_OwnerId",
                table: "monitors",
                column: "OwnerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM monitors) THEN
                        RAISE EXCEPTION 'Reverting monitor ownership would discard owner data.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_monitors_AspNetUsers_OwnerId",
                table: "monitors");

            migrationBuilder.DropIndex(
                name: "IX_monitors_OwnerId_Slug",
                table: "monitors");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "monitors");

            migrationBuilder.CreateIndex(
                name: "IX_monitors_Slug",
                table: "monitors",
                column: "Slug",
                unique: true);
        }
    }
}
