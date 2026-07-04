using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.SourceService.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSourceSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "source");

            migrationBuilder.CreateTable(
                name: "source_jobs",
                schema: "source",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    SourceBranch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    FileSize = table.Column<long>(type: "bigint", nullable: true),
                    HashSha256 = table.Column<string>(type: "text", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    FetchStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FetchCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_jobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_source_jobs_PackageName",
                schema: "source",
                table: "source_jobs",
                column: "PackageName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "source_jobs",
                schema: "source");
        }
    }
}
