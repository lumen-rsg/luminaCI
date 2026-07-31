using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.RepositoryService.Data.Migrations
{
    /// <inheritdoc />
    public partial class PublishPromotionSetsAtomically : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PromotionSets_gate_bundle",
                table: "PromotionSets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PromotionSets_state",
                table: "PromotionSets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Packages_candidate_identity",
                table: "Packages");

            migrationBuilder.AddColumn<string>(
                name: "GateBaselineManifestSha256",
                table: "PromotionSets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromotedRepositoryManifestSha256",
                table: "PromotionSets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RollbackReason",
                table: "PromotionSets",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RollbackSnapshotPath",
                table: "PromotionSets",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RolledBackAt",
                table: "PromotionSets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RolledBackBy",
                table: "PromotionSets",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            // Legacy bundles predate the baseline fingerprint and must never be
            // promoted by the new reconciler. A zero digest satisfies the
            // structural invariant while guaranteeing the drift check fails.
            migrationBuilder.Sql(
                "UPDATE \"PromotionSets\" SET \"GateBaselineManifestSha256\" = repeat('0', 64) " +
                "WHERE \"GateBundleObjectName\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PromotionSets_gate_bundle",
                table: "PromotionSets",
                sql: "((\"GateBundleObjectName\" IS NULL AND \"GateCandidateManifestSha256\" IS NULL AND \"GateBaselineManifestSha256\" IS NULL AND \"GateBundleSha256\" IS NULL AND \"GateBundleSize\" IS NULL AND \"GateBundlePreparedAt\" IS NULL) OR (\"GateBundleObjectName\" IS NOT NULL AND \"GateCandidateManifestSha256\" IS NOT NULL AND \"GateBaselineManifestSha256\" IS NOT NULL AND \"GateBundleSha256\" IS NOT NULL AND \"GateBundleSize\" > 0 AND \"GateBundlePreparedAt\" IS NOT NULL)) AND (\"Status\" IN (0, 3) OR \"GateBundleObjectName\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PromotionSets_publication",
                table: "PromotionSets",
                sql: "(\"Status\" IN (0, 1, 2, 3) AND \"PromotedRepositoryManifestSha256\" IS NULL AND \"RollbackSnapshotPath\" IS NULL AND \"RolledBackAt\" IS NULL AND \"RolledBackBy\" IS NULL AND \"RollbackReason\" IS NULL) OR (\"Status\" = 4 AND \"PromotedRepositoryManifestSha256\" IS NOT NULL AND \"RollbackSnapshotPath\" IS NOT NULL AND \"RolledBackAt\" IS NULL AND \"RolledBackBy\" IS NULL AND \"RollbackReason\" IS NULL) OR (\"Status\" = 5 AND \"PromotedRepositoryManifestSha256\" IS NOT NULL AND \"RollbackSnapshotPath\" IS NULL AND \"RolledBackAt\" IS NOT NULL AND \"RolledBackBy\" IS NOT NULL AND \"RollbackReason\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PromotionSets_state",
                table: "PromotionSets",
                sql: "(\"Status\" = 0 AND \"GateJobName\" IS NULL AND \"GateJobUid\" IS NULL AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 1 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 2 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NOT NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 3 AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NOT NULL AND \"PromotedAt\" IS NULL AND ((\"GateJobName\" IS NULL AND \"GateJobUid\" IS NULL) OR (\"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL))) OR (\"Status\" IN (4, 5) AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NOT NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Packages_candidate_identity",
                table: "Packages",
                sql: "(\"PromotionSetId\" IS NULL AND \"PromotionPackageId\" IS NULL AND \"CandidateObjectName\" IS NULL AND \"Status\" NOT IN ('Candidate', 'RolledBack')) OR (\"PromotionSetId\" IS NOT NULL AND \"PromotionPackageId\" IS NOT NULL AND \"CandidateObjectName\" IS NOT NULL AND \"Status\" IN ('Candidate', 'Ready', 'RolledBack'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PromotionSets_gate_bundle",
                table: "PromotionSets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PromotionSets_publication",
                table: "PromotionSets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PromotionSets_state",
                table: "PromotionSets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Packages_candidate_identity",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "GateBaselineManifestSha256",
                table: "PromotionSets");

            migrationBuilder.DropColumn(
                name: "PromotedRepositoryManifestSha256",
                table: "PromotionSets");

            migrationBuilder.DropColumn(
                name: "RollbackReason",
                table: "PromotionSets");

            migrationBuilder.DropColumn(
                name: "RollbackSnapshotPath",
                table: "PromotionSets");

            migrationBuilder.DropColumn(
                name: "RolledBackAt",
                table: "PromotionSets");

            migrationBuilder.DropColumn(
                name: "RolledBackBy",
                table: "PromotionSets");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PromotionSets_gate_bundle",
                table: "PromotionSets",
                sql: "((\"GateBundleObjectName\" IS NULL AND \"GateCandidateManifestSha256\" IS NULL AND \"GateBundleSha256\" IS NULL AND \"GateBundleSize\" IS NULL AND \"GateBundlePreparedAt\" IS NULL) OR (\"GateBundleObjectName\" IS NOT NULL AND \"GateCandidateManifestSha256\" IS NOT NULL AND \"GateBundleSha256\" IS NOT NULL AND \"GateBundleSize\" > 0 AND \"GateBundlePreparedAt\" IS NOT NULL)) AND (\"Status\" IN (0, 3) OR \"GateBundleObjectName\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PromotionSets_state",
                table: "PromotionSets",
                sql: "(\"Status\" = 0 AND \"GateJobName\" IS NULL AND \"GateJobUid\" IS NULL AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 1 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 2 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NOT NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR (\"Status\" = 3 AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NOT NULL AND \"PromotedAt\" IS NULL AND ((\"GateJobName\" IS NULL AND \"GateJobUid\" IS NULL) OR (\"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL))) OR (\"Status\" = 4 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NOT NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Packages_candidate_identity",
                table: "Packages",
                sql: "(\"PromotionSetId\" IS NULL AND \"PromotionPackageId\" IS NULL AND \"CandidateObjectName\" IS NULL AND \"Status\" <> 'Candidate') OR (\"PromotionSetId\" IS NOT NULL AND \"PromotionPackageId\" IS NOT NULL AND \"CandidateObjectName\" IS NOT NULL AND \"Status\" IN ('Candidate', 'Ready'))");
        }
    }
}
