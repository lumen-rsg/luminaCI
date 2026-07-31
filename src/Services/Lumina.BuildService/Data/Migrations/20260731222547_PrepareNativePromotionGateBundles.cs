using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.BuildService.Data.Migrations
{
    /// <inheritdoc />
    public partial class PrepareNativePromotionGateBundles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_native_promotion_gates_state",
                schema: "build",
                table: "native_promotion_gates");

            migrationBuilder.AddColumn<string>(
                name: "BundleObjectName",
                schema: "build",
                table: "native_promotion_gates",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BundlePreparedAt",
                schema: "build",
                table: "native_promotion_gates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BundleSha256",
                schema: "build",
                table: "native_promotion_gates",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BundleSize",
                schema: "build",
                table: "native_promotion_gates",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PreparationRequestedAt",
                schema: "build",
                table: "native_promotion_gates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_native_promotion_gates_bundle",
                schema: "build",
                table: "native_promotion_gates",
                sql: "((\"BundleObjectName\" IS NULL AND \"BundleSha256\" IS NULL AND \"BundleSize\" IS NULL AND \"BundlePreparedAt\" IS NULL) OR (\"BundleObjectName\" IS NOT NULL AND \"BundleSha256\" IS NOT NULL AND \"BundleSize\" > 0 AND \"BundlePreparedAt\" IS NOT NULL)) AND (\"Status\" IN (0, 3) OR \"BundleObjectName\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_native_promotion_gates_state",
                schema: "build",
                table: "native_promotion_gates",
                sql: "(\"Status\" = 0 AND \"KubernetesNamespace\" IS NULL AND \"KubernetesJobName\" IS NULL AND \"KubernetesJobUid\" IS NULL AND \"KubernetesPodName\" IS NULL AND \"ResultSha256\" IS NULL AND \"FailureReason\" IS NULL AND \"StartedAt\" IS NULL AND \"CompletedAt\" IS NULL) OR (\"Status\" = 1 AND \"KubernetesNamespace\" IS NOT NULL AND \"KubernetesJobName\" IS NOT NULL AND \"KubernetesJobUid\" IS NOT NULL AND \"ResultSha256\" IS NULL AND \"FailureReason\" IS NULL AND \"StartedAt\" IS NOT NULL AND \"CompletedAt\" IS NULL) OR (\"Status\" = 2 AND \"KubernetesNamespace\" IS NOT NULL AND \"KubernetesJobName\" IS NOT NULL AND \"KubernetesJobUid\" IS NOT NULL AND \"ResultSha256\" IS NOT NULL AND \"FailureReason\" IS NULL AND \"StartedAt\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL) OR (\"Status\" = 3 AND \"ResultSha256\" IS NULL AND \"FailureReason\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL AND ((\"KubernetesNamespace\" IS NULL AND \"KubernetesJobName\" IS NULL AND \"KubernetesJobUid\" IS NULL AND \"KubernetesPodName\" IS NULL AND \"StartedAt\" IS NULL) OR (\"KubernetesNamespace\" IS NOT NULL AND \"KubernetesJobName\" IS NOT NULL AND \"KubernetesJobUid\" IS NOT NULL AND \"StartedAt\" IS NOT NULL)))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_native_promotion_gates_bundle",
                schema: "build",
                table: "native_promotion_gates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_native_promotion_gates_state",
                schema: "build",
                table: "native_promotion_gates");

            migrationBuilder.DropColumn(
                name: "BundleObjectName",
                schema: "build",
                table: "native_promotion_gates");

            migrationBuilder.DropColumn(
                name: "BundlePreparedAt",
                schema: "build",
                table: "native_promotion_gates");

            migrationBuilder.DropColumn(
                name: "BundleSha256",
                schema: "build",
                table: "native_promotion_gates");

            migrationBuilder.DropColumn(
                name: "BundleSize",
                schema: "build",
                table: "native_promotion_gates");

            migrationBuilder.DropColumn(
                name: "PreparationRequestedAt",
                schema: "build",
                table: "native_promotion_gates");

            migrationBuilder.AddCheckConstraint(
                name: "CK_native_promotion_gates_state",
                schema: "build",
                table: "native_promotion_gates",
                sql: "(\"Status\" = 0 AND \"KubernetesNamespace\" IS NULL AND \"KubernetesJobName\" IS NULL AND \"KubernetesJobUid\" IS NULL AND \"KubernetesPodName\" IS NULL AND \"ResultSha256\" IS NULL AND \"FailureReason\" IS NULL AND \"StartedAt\" IS NULL AND \"CompletedAt\" IS NULL) OR (\"Status\" = 1 AND \"KubernetesNamespace\" IS NOT NULL AND \"KubernetesJobName\" IS NOT NULL AND \"KubernetesJobUid\" IS NOT NULL AND \"ResultSha256\" IS NULL AND \"FailureReason\" IS NULL AND \"StartedAt\" IS NOT NULL AND \"CompletedAt\" IS NULL) OR (\"Status\" = 2 AND \"KubernetesNamespace\" IS NOT NULL AND \"KubernetesJobName\" IS NOT NULL AND \"KubernetesJobUid\" IS NOT NULL AND \"ResultSha256\" IS NOT NULL AND \"FailureReason\" IS NULL AND \"StartedAt\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL) OR (\"Status\" = 3 AND \"KubernetesNamespace\" IS NOT NULL AND \"KubernetesJobName\" IS NOT NULL AND \"KubernetesJobUid\" IS NOT NULL AND \"ResultSha256\" IS NULL AND \"FailureReason\" IS NOT NULL AND \"StartedAt\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL)");
        }
    }
}
