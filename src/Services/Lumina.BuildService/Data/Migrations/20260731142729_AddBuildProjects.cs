using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.BuildService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "build_projects",
                schema: "build",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    GitRepoUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    GitBranch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ManifestPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WebhookSecret = table.Column<string>(type: "text", nullable: false),
                    GitUsername = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    GitToken = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_build_projects", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_build_projects_Name",
                schema: "build",
                table: "build_projects",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "build_projects",
                schema: "build");
        }
    }
}
