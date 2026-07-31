using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.RepositoryService.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackPromotionPackageMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Packages_candidate_identity",
                table: "Packages");

            migrationBuilder.AddColumn<string>(
                name: "PromotionPackageId",
                table: "Packages",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Packages_candidate_identity",
                table: "Packages",
                sql: "(\"PromotionSetId\" IS NULL AND \"PromotionPackageId\" IS NULL AND \"CandidateObjectName\" IS NULL AND \"Status\" <> 'Candidate') OR (\"PromotionSetId\" IS NOT NULL AND \"PromotionPackageId\" IS NOT NULL AND \"CandidateObjectName\" IS NOT NULL AND \"Status\" IN ('Candidate', 'Ready'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Packages_candidate_identity",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "PromotionPackageId",
                table: "Packages");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Packages_candidate_identity",
                table: "Packages",
                sql: "(\"PromotionSetId\" IS NULL AND \"CandidateObjectName\" IS NULL AND \"Status\" <> 'Candidate') OR (\"PromotionSetId\" IS NOT NULL AND \"CandidateObjectName\" IS NOT NULL AND \"Status\" IN ('Candidate', 'Ready'))");
        }
    }
}
