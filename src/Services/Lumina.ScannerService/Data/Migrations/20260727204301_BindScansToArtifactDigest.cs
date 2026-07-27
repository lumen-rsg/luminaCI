using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.ScannerService.Data.Migrations
{
    /// <inheritdoc />
    public partial class BindScansToArtifactDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArtifactSha256",
                table: "CveReports",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArtifactSha256",
                table: "CveReports");
        }
    }
}
