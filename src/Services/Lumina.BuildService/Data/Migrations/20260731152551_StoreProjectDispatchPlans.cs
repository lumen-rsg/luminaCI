using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.BuildService.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoreProjectDispatchPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DispatchPlanJson",
                schema: "build",
                table: "project_webhook_deliveries",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManifestSha256",
                schema: "build",
                table: "project_webhook_deliveries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SnapshotFileSize",
                schema: "build",
                table: "project_webhook_deliveries",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DispatchPlanJson",
                schema: "build",
                table: "project_webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "ManifestSha256",
                schema: "build",
                table: "project_webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "SnapshotFileSize",
                schema: "build",
                table: "project_webhook_deliveries");
        }
    }
}
