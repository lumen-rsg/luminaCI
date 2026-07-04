using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.SecurityService.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSecuritySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HashRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Md5 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Sha1 = table.Column<string>(type: "text", nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HashRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecurityKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    KeyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PublicKey = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SigningRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactPath = table.Column<string>(type: "text", nullable: false),
                    SignaturePath = table.Column<string>(type: "text", nullable: false),
                    KeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SignatureContent = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigningRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HashRecords_FileName",
                table: "HashRecords",
                column: "FileName");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityKeys_KeyId",
                table: "SecurityKeys",
                column: "KeyId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HashRecords");

            migrationBuilder.DropTable(
                name: "SecurityKeys");

            migrationBuilder.DropTable(
                name: "SigningRequests");
        }
    }
}
