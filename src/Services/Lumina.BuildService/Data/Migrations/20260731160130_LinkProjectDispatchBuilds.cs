using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.BuildService.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkProjectDispatchBuilds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RepositoryUrl",
                schema: "build",
                table: "project_webhook_deliveries",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE build.project_webhook_deliveries AS delivery
                SET "RepositoryUrl" = project."GitRepoUrl"
                FROM build.build_projects AS project
                WHERE delivery."BuildProjectId" = project."Id";
                """);

            migrationBuilder.AlterColumn<string>(
                name: "RepositoryUrl",
                schema: "build",
                table: "project_webhook_deliveries",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProjectPackageId",
                schema: "build",
                table: "build_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProjectStageOrder",
                schema: "build",
                table: "build_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectWebhookDeliveryId",
                schema: "build",
                table: "build_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_webhook_deliveries_Status_UpdatedAt",
                schema: "build",
                table: "project_webhook_deliveries",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_build_jobs_ProjectWebhookDeliveryId_ProjectPackageId",
                schema: "build",
                table: "build_jobs",
                columns: new[] { "ProjectWebhookDeliveryId", "ProjectPackageId" },
                unique: true,
                filter: "\"ProjectWebhookDeliveryId\" IS NOT NULL AND \"ProjectPackageId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_build_jobs_project_dispatch_binding",
                schema: "build",
                table: "build_jobs",
                sql: "(\"ProjectWebhookDeliveryId\" IS NULL AND \"ProjectPackageId\" IS NULL AND \"ProjectStageOrder\" IS NULL) OR (\"ProjectWebhookDeliveryId\" IS NOT NULL AND \"ProjectPackageId\" IS NOT NULL AND \"ProjectStageOrder\" IS NOT NULL AND \"ProjectStageOrder\" >= 0)");

            migrationBuilder.AddForeignKey(
                name: "FK_build_jobs_project_webhook_deliveries_ProjectWebhookDeliver~",
                schema: "build",
                table: "build_jobs",
                column: "ProjectWebhookDeliveryId",
                principalSchema: "build",
                principalTable: "project_webhook_deliveries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_build_jobs_project_webhook_deliveries_ProjectWebhookDeliver~",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropIndex(
                name: "IX_project_webhook_deliveries_Status_UpdatedAt",
                schema: "build",
                table: "project_webhook_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_build_jobs_ProjectWebhookDeliveryId_ProjectPackageId",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_build_jobs_project_dispatch_binding",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropColumn(
                name: "RepositoryUrl",
                schema: "build",
                table: "project_webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "ProjectPackageId",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropColumn(
                name: "ProjectStageOrder",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropColumn(
                name: "ProjectWebhookDeliveryId",
                schema: "build",
                table: "build_jobs");
        }
    }
}
