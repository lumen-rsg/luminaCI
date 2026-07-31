using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.BuildService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectWebhookDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_webhook_deliveries",
                schema: "build",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderDeliveryId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Branch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ChangedPaths = table.Column<List<string>>(type: "text[]", nullable: false),
                    CommitAuthor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CommitMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SourceJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    SnapshotStoragePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    SnapshotSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_webhook_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_project_webhook_deliveries_build_projects_BuildProjectId",
                        column: x => x.BuildProjectId,
                        principalSchema: "build",
                        principalTable: "build_projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_webhook_deliveries_BuildProjectId_ProviderDeliveryId",
                schema: "build",
                table: "project_webhook_deliveries",
                columns: new[] { "BuildProjectId", "ProviderDeliveryId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_webhook_deliveries",
                schema: "build");
        }
    }
}
