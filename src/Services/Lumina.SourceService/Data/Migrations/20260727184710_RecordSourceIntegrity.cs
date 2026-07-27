using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.SourceService.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordSourceIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExpectedSha256",
                schema: "source",
                table: "source_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedRevision",
                schema: "source",
                table: "source_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedUrl",
                schema: "source",
                table: "source_jobs",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpectedSha256",
                schema: "source",
                table: "source_jobs");

            migrationBuilder.DropColumn(
                name: "ResolvedRevision",
                schema: "source",
                table: "source_jobs");

            migrationBuilder.DropColumn(
                name: "ResolvedUrl",
                schema: "source",
                table: "source_jobs");
        }
    }
}
