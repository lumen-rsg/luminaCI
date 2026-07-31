using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.RepositoryService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryPromotionSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CandidateObjectName",
                table: "Packages",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PromotionSetId",
                table: "Packages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PromotionSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    PromotionGroup = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TargetArchitecture = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    GateRunnerImageDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GateJobName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    GateJobUid = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    GateResultSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    GateCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    PromotedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromotionSets", x => x.Id);
                    table.UniqueConstraint("AK_PromotionSets_Id_RepositoryId", x => new { x.Id, x.RepositoryId });
                    table.CheckConstraint("CK_PromotionSets_state", "(\"Status\" = 0 AND \"GateJobName\" IS NULL AND \"GateJobUid\" IS NULL AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 1 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 2 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NOT NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 3 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NOT NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 4 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NOT NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PromotionSets_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Packages_PromotionSetId_RepositoryId",
                table: "Packages",
                columns: new[] { "PromotionSetId", "RepositoryId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Packages_candidate_identity",
                table: "Packages",
                sql: "(\"PromotionSetId\" IS NULL AND \"CandidateObjectName\" IS NULL AND \"Status\" <> 'Candidate') OR (\"PromotionSetId\" IS NOT NULL AND \"CandidateObjectName\" IS NOT NULL AND \"Status\" IN ('Candidate', 'Ready'))");

            migrationBuilder.CreateIndex(
                name: "IX_PromotionSets_RepositoryId_PromotionGroup_CreatedAt",
                table: "PromotionSets",
                columns: new[] { "RepositoryId", "PromotionGroup", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PromotionSets_RepositoryId_Status",
                table: "PromotionSets",
                columns: new[] { "RepositoryId", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_Packages_PromotionSets_PromotionSetId_RepositoryId",
                table: "Packages",
                columns: new[] { "PromotionSetId", "RepositoryId" },
                principalTable: "PromotionSets",
                principalColumns: new[] { "Id", "RepositoryId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Packages_PromotionSets_PromotionSetId_RepositoryId",
                table: "Packages");

            migrationBuilder.DropTable(
                name: "PromotionSets");

            migrationBuilder.DropIndex(
                name: "IX_Packages_PromotionSetId_RepositoryId",
                table: "Packages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Packages_candidate_identity",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "CandidateObjectName",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "PromotionSetId",
                table: "Packages");
        }
    }
}
