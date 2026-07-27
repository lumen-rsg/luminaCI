using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.SourceService.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceIniWithPackageCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PackageRevisionId",
                schema: "source",
                table: "source_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "package_definitions",
                schema: "source",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveRevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_package_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "package_revisions",
                schema: "source",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    SourceReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ExpectedSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SpecPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    BuildImage = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_package_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_package_revisions_package_definitions_PackageDefinitionId",
                        column: x => x.PackageDefinitionId,
                        principalSchema: "source",
                        principalTable: "package_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_source_jobs_PackageRevisionId",
                schema: "source",
                table: "source_jobs",
                column: "PackageRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_package_definitions_Slug",
                schema: "source",
                table: "package_definitions",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_package_revisions_PackageDefinitionId_RevisionNumber",
                schema: "source",
                table: "package_revisions",
                columns: new[] { "PackageDefinitionId", "RevisionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_source_jobs_package_revisions_PackageRevisionId",
                schema: "source",
                table: "source_jobs",
                column: "PackageRevisionId",
                principalSchema: "source",
                principalTable: "package_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_source_jobs_package_revisions_PackageRevisionId",
                schema: "source",
                table: "source_jobs");

            migrationBuilder.DropTable(
                name: "package_revisions",
                schema: "source");

            migrationBuilder.DropTable(
                name: "package_definitions",
                schema: "source");

            migrationBuilder.DropIndex(
                name: "IX_source_jobs_PackageRevisionId",
                schema: "source",
                table: "source_jobs");

            migrationBuilder.DropColumn(
                name: "PackageRevisionId",
                schema: "source",
                table: "source_jobs");
        }
    }
}
