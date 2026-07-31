using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.BuildService.Data.Migrations
{
    /// <inheritdoc />
    public partial class BindProjectPipelines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BuildProjectId",
                schema: "build",
                table: "pipelines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PackageId",
                schema: "build",
                table: "pipelines",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_pipelines_BuildProjectId_PackageId",
                schema: "build",
                table: "pipelines",
                columns: new[] { "BuildProjectId", "PackageId" },
                unique: true,
                filter: "\"BuildProjectId\" IS NOT NULL AND \"PackageId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_pipelines_project_package_binding",
                schema: "build",
                table: "pipelines",
                sql: "(\"BuildProjectId\" IS NULL AND \"PackageId\" IS NULL) OR (\"BuildProjectId\" IS NOT NULL AND \"PackageId\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_pipelines_build_projects_BuildProjectId",
                schema: "build",
                table: "pipelines",
                column: "BuildProjectId",
                principalSchema: "build",
                principalTable: "build_projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_pipelines_build_projects_BuildProjectId",
                schema: "build",
                table: "pipelines");

            migrationBuilder.DropIndex(
                name: "IX_pipelines_BuildProjectId_PackageId",
                schema: "build",
                table: "pipelines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_pipelines_project_package_binding",
                schema: "build",
                table: "pipelines");

            migrationBuilder.DropColumn(
                name: "BuildProjectId",
                schema: "build",
                table: "pipelines");

            migrationBuilder.DropColumn(
                name: "PackageId",
                schema: "build",
                table: "pipelines");
        }
    }
}
