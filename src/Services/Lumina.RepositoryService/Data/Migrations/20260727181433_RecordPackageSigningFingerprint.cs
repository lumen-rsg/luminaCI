using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.RepositoryService.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordPackageSigningFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SigningKeyFingerprint",
                table: "Packages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SigningKeyFingerprint",
                table: "Packages");
        }
    }
}
