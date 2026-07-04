using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.ScannerService.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialScannerSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CveReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScannerType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ScannedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CriticalCount = table.Column<int>(type: "integer", nullable: false),
                    HighCount = table.Column<int>(type: "integer", nullable: false),
                    MediumCount = table.Column<int>(type: "integer", nullable: false),
                    LowCount = table.Column<int>(type: "integer", nullable: false),
                    UnknownCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    RawOutput = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CveReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Vulnerabilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CveReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    CveId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Package = table.Column<string>(type: "text", nullable: false),
                    PackageName = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FixedVersion = table.Column<string>(type: "text", nullable: true),
                    InstalledVersion = table.Column<string>(type: "text", nullable: false),
                    References = table.Column<string[]>(type: "text[]", nullable: false),
                    PublishedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vulnerabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Vulnerabilities_CveReports_CveReportId",
                        column: x => x.CveReportId,
                        principalTable: "CveReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Vulnerabilities_CveId",
                table: "Vulnerabilities",
                column: "CveId");

            migrationBuilder.CreateIndex(
                name: "IX_Vulnerabilities_CveReportId",
                table: "Vulnerabilities",
                column: "CveReportId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Vulnerabilities");

            migrationBuilder.DropTable(
                name: "CveReports");
        }
    }
}
