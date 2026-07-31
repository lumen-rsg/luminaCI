using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.RepositoryService.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordPromotionGateBundles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PromotionSets_state",
                table: "PromotionSets");

            migrationBuilder.AddColumn<string>(
                name: "GateBundleObjectName",
                table: "PromotionSets",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GateBundlePreparedAt",
                table: "PromotionSets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GateBundleSha256",
                table: "PromotionSets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "GateBundleSize",
                table: "PromotionSets",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GateCandidateManifestSha256",
                table: "PromotionSets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PromotionSets_gate_bundle",
                table: "PromotionSets",
                sql: "((\"GateBundleObjectName\" IS NULL AND \"GateCandidateManifestSha256\" IS NULL AND \"GateBundleSha256\" IS NULL AND \"GateBundleSize\" IS NULL AND \"GateBundlePreparedAt\" IS NULL) OR (\"GateBundleObjectName\" IS NOT NULL AND \"GateCandidateManifestSha256\" IS NOT NULL AND \"GateBundleSha256\" IS NOT NULL AND \"GateBundleSize\" > 0 AND \"GateBundlePreparedAt\" IS NOT NULL)) AND (\"Status\" IN (0, 3) OR \"GateBundleObjectName\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PromotionSets_state",
                table: "PromotionSets",
                sql: "(\"Status\" = 0 AND \"GateJobName\" IS NULL AND \"GateJobUid\" IS NULL AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 1 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 2 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NOT NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 3 AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NOT NULL AND \"PromotedAt\" IS NULL AND ((\"GateJobName\" IS NULL AND \"GateJobUid\" IS NULL) OR (\"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL))) OR (\"Status\" = 4 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NOT NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PromotionSets_gate_bundle",
                table: "PromotionSets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PromotionSets_state",
                table: "PromotionSets");

            migrationBuilder.DropColumn(
                name: "GateBundleObjectName",
                table: "PromotionSets");

            migrationBuilder.DropColumn(
                name: "GateBundlePreparedAt",
                table: "PromotionSets");

            migrationBuilder.DropColumn(
                name: "GateBundleSha256",
                table: "PromotionSets");

            migrationBuilder.DropColumn(
                name: "GateBundleSize",
                table: "PromotionSets");

            migrationBuilder.DropColumn(
                name: "GateCandidateManifestSha256",
                table: "PromotionSets");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PromotionSets_state",
                table: "PromotionSets",
                sql: "(\"Status\" = 0 AND \"GateJobName\" IS NULL AND \"GateJobUid\" IS NULL AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 1 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 2 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NOT NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 3 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NOT NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 4 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NOT NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NOT NULL)");
        }
    }
}
