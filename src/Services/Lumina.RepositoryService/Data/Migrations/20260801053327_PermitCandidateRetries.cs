using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.RepositoryService.Data.Migrations
{
    /// <inheritdoc />
    public partial class PermitCandidateRetries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Packages_RepositoryId_FileName",
                table: "Packages");

            migrationBuilder.DropIndex(
                name: "IX_Packages_RepositoryId_Name_Version_Release_Arch",
                table: "Packages");

            migrationBuilder.CreateIndex(
                name: "IX_Packages_RepositoryId_FileName",
                table: "Packages",
                columns: new[] { "RepositoryId", "FileName" },
                unique: true,
                filter: "\"Status\" = 'Ready'");

            migrationBuilder.CreateIndex(
                name: "IX_Packages_RepositoryId_Name_Version_Release_Arch",
                table: "Packages",
                columns: new[] { "RepositoryId", "Name", "Version", "Release", "Arch" },
                unique: true,
                filter: "\"Status\" = 'Ready'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Packages_RepositoryId_FileName",
                table: "Packages");

            migrationBuilder.DropIndex(
                name: "IX_Packages_RepositoryId_Name_Version_Release_Arch",
                table: "Packages");

            migrationBuilder.CreateIndex(
                name: "IX_Packages_RepositoryId_FileName",
                table: "Packages",
                columns: new[] { "RepositoryId", "FileName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Packages_RepositoryId_Name_Version_Release_Arch",
                table: "Packages",
                columns: new[] { "RepositoryId", "Name", "Version", "Release", "Arch" },
                unique: true);
        }
    }
}
