using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.RepositoryService.Data.Migrations
{
    /// <inheritdoc />
    public partial class PublishRepositoriesTransactionally : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Packages_RepositoryId_Name_Version_Arch",
                table: "Packages");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Packages",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Ready");

            // Retain the newest legacy row for each identity before enforcing
            // idempotency. Earlier consumers could insert duplicates freely.
            migrationBuilder.Sql(
                """
                DELETE FROM "Packages" older
                USING "Packages" newer
                WHERE older."RepositoryId" = newer."RepositoryId"
                  AND older."ArtifactId" IS NOT NULL
                  AND older."ArtifactId" = newer."ArtifactId"
                  AND (older."PublishedAt", older."Id") < (newer."PublishedAt", newer."Id");

                DELETE FROM "Packages" older
                USING "Packages" newer
                WHERE older."RepositoryId" = newer."RepositoryId"
                  AND older."FileName" = newer."FileName"
                  AND (older."PublishedAt", older."Id") < (newer."PublishedAt", newer."Id");

                DELETE FROM "Packages" older
                USING "Packages" newer
                WHERE older."RepositoryId" = newer."RepositoryId"
                  AND older."Name" = newer."Name"
                  AND older."Version" = newer."Version"
                  AND older."Release" = newer."Release"
                  AND older."Arch" = newer."Arch"
                  AND (older."PublishedAt", older."Id") < (newer."PublishedAt", newer."Id");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Packages_RepositoryId_ArtifactId",
                table: "Packages",
                columns: new[] { "RepositoryId", "ArtifactId" },
                unique: true,
                filter: "\"ArtifactId\" IS NOT NULL");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Packages_RepositoryId_ArtifactId",
                table: "Packages");

            migrationBuilder.DropIndex(
                name: "IX_Packages_RepositoryId_FileName",
                table: "Packages");

            migrationBuilder.DropIndex(
                name: "IX_Packages_RepositoryId_Name_Version_Release_Arch",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Packages");

            migrationBuilder.CreateIndex(
                name: "IX_Packages_RepositoryId_Name_Version_Arch",
                table: "Packages",
                columns: new[] { "RepositoryId", "Name", "Version", "Arch" });
        }
    }
}
