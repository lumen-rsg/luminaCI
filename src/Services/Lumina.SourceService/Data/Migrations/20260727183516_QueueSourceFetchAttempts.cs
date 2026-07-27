using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.SourceService.Data.Migrations
{
    /// <inheritdoc />
    public partial class QueueSourceFetchAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancellationRequested",
                schema: "source",
                table: "source_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "HeartbeatAt",
                schema: "source",
                table: "source_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresAt",
                schema: "source",
                table: "source_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                schema: "source",
                table: "source_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxRetries",
                schema: "source",
                table: "source_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.CreateIndex(
                name: "IX_source_jobs_Status_LeaseExpiresAt_CreatedAt",
                schema: "source",
                table: "source_jobs",
                columns: new[] { "Status", "LeaseExpiresAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_source_jobs_Status_LeaseExpiresAt_CreatedAt",
                schema: "source",
                table: "source_jobs");

            migrationBuilder.DropColumn(
                name: "CancellationRequested",
                schema: "source",
                table: "source_jobs");

            migrationBuilder.DropColumn(
                name: "HeartbeatAt",
                schema: "source",
                table: "source_jobs");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                schema: "source",
                table: "source_jobs");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                schema: "source",
                table: "source_jobs");

            migrationBuilder.DropColumn(
                name: "MaxRetries",
                schema: "source",
                table: "source_jobs");
        }
    }
}
