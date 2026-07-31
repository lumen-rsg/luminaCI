using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.BuildService.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordCandidateAcknowledgements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CandidatePackageId",
                schema: "build",
                table: "build_artifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CandidateRepositoryId",
                schema: "build",
                table: "build_artifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CandidateStagedAt",
                schema: "build",
                table: "build_artifacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PromotionSetId",
                schema: "build",
                table: "build_artifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_build_artifacts_PromotionSetId_CandidateStagedAt",
                schema: "build",
                table: "build_artifacts",
                columns: new[] { "PromotionSetId", "CandidateStagedAt" },
                filter: "\"PromotionSetId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_build_artifacts_candidate_identity",
                schema: "build",
                table: "build_artifacts",
                sql: "(\"CandidateRepositoryId\" IS NULL AND \"CandidatePackageId\" IS NULL AND \"PromotionSetId\" IS NULL AND \"CandidateStagedAt\" IS NULL) OR (\"CandidateRepositoryId\" IS NOT NULL AND \"CandidatePackageId\" IS NOT NULL AND \"PromotionSetId\" IS NOT NULL AND \"CandidateStagedAt\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_build_artifacts_PromotionSetId_CandidateStagedAt",
                schema: "build",
                table: "build_artifacts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_build_artifacts_candidate_identity",
                schema: "build",
                table: "build_artifacts");

            migrationBuilder.DropColumn(
                name: "CandidatePackageId",
                schema: "build",
                table: "build_artifacts");

            migrationBuilder.DropColumn(
                name: "CandidateRepositoryId",
                schema: "build",
                table: "build_artifacts");

            migrationBuilder.DropColumn(
                name: "CandidateStagedAt",
                schema: "build",
                table: "build_artifacts");

            migrationBuilder.DropColumn(
                name: "PromotionSetId",
                schema: "build",
                table: "build_artifacts");
        }
    }
}
