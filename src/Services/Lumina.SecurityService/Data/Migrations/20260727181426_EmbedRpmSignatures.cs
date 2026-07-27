using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.SecurityService.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmbedRpmSignatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SignatureContent",
                table: "SigningRequests");

            migrationBuilder.DropColumn(
                name: "SignaturePath",
                table: "SigningRequests");

            migrationBuilder.AddColumn<string>(
                name: "ExpectedSha256",
                table: "SigningRequests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "KeyFingerprint",
                table: "SigningRequests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "SignedFileSize",
                table: "SigningRequests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignedSha256",
                table: "SigningRequests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Legacy code could mark every generated key active and overwrite
            // the same artifact's signing row repeatedly. Preserve the newest
            // records before enforcing the new single-active/idempotent policy.
            migrationBuilder.Sql(
                """
                UPDATE "SecurityKeys"
                SET "IsActive" = FALSE
                WHERE "IsActive" = TRUE
                  AND "Id" NOT IN (
                    SELECT "Id"
                    FROM "SecurityKeys"
                    WHERE "IsActive" = TRUE
                    ORDER BY "CreatedAt" DESC, "Id" DESC
                    LIMIT 1
                  );

                DELETE FROM "SigningRequests" older
                USING "SigningRequests" newer
                WHERE older."ArtifactId" = newer."ArtifactId"
                  AND (older."CreatedAt", older."Id") < (newer."CreatedAt", newer."Id");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SigningRequests_ArtifactId",
                table: "SigningRequests",
                column: "ArtifactId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityKeys_IsActive",
                table: "SecurityKeys",
                column: "IsActive",
                unique: true,
                filter: "\"IsActive\" = TRUE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SigningRequests_ArtifactId",
                table: "SigningRequests");

            migrationBuilder.DropIndex(
                name: "IX_SecurityKeys_IsActive",
                table: "SecurityKeys");

            migrationBuilder.DropColumn(
                name: "ExpectedSha256",
                table: "SigningRequests");

            migrationBuilder.DropColumn(
                name: "KeyFingerprint",
                table: "SigningRequests");

            migrationBuilder.DropColumn(
                name: "SignedFileSize",
                table: "SigningRequests");

            migrationBuilder.DropColumn(
                name: "SignedSha256",
                table: "SigningRequests");

            migrationBuilder.AddColumn<string>(
                name: "SignatureContent",
                table: "SigningRequests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignaturePath",
                table: "SigningRequests",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
