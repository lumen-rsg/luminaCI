using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.BuildService.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordKubernetesArtifactProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_build_jobs_executor_identity",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropIndex(
                name: "IX_build_artifacts_BuildJobId",
                schema: "build",
                table: "build_artifacts");

            migrationBuilder.AddColumn<string>(
                name: "KubernetesArtifactManifestSha256",
                schema: "build",
                table: "build_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "KubernetesArtifactsImportedAt",
                schema: "build",
                table: "build_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RpmNevra",
                schema: "build",
                table: "build_artifacts",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceStoragePath",
                schema: "build",
                table: "build_artifacts",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_build_jobs_executor_identity",
                schema: "build",
                table: "build_jobs",
                sql: "((\"ExecutionBackend\" = 0 AND \"KubernetesNamespace\" IS NULL AND \"KubernetesJobName\" IS NULL AND \"KubernetesJobUid\" IS NULL AND \"KubernetesPodName\" IS NULL AND \"KubernetesArtifactManifestSha256\" IS NULL AND \"KubernetesArtifactsImportedAt\" IS NULL) OR (\"ExecutionBackend\" = 1 AND ((\"KubernetesNamespace\" IS NULL AND \"KubernetesJobName\" IS NULL AND \"KubernetesJobUid\" IS NULL AND \"KubernetesPodName\" IS NULL AND \"KubernetesArtifactManifestSha256\" IS NULL AND \"KubernetesArtifactsImportedAt\" IS NULL) OR (\"KubernetesNamespace\" IS NOT NULL AND \"KubernetesJobName\" IS NOT NULL)))) AND ((\"KubernetesArtifactManifestSha256\" IS NULL AND \"KubernetesArtifactsImportedAt\" IS NULL) OR (\"KubernetesArtifactManifestSha256\" IS NOT NULL AND \"KubernetesArtifactsImportedAt\" IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_build_artifacts_BuildJobId_FileName",
                schema: "build",
                table: "build_artifacts",
                columns: new[] { "BuildJobId", "FileName" },
                unique: true,
                filter: "\"SourceStoragePath\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_build_jobs_executor_identity",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropIndex(
                name: "IX_build_artifacts_BuildJobId_FileName",
                schema: "build",
                table: "build_artifacts");

            migrationBuilder.DropColumn(
                name: "KubernetesArtifactManifestSha256",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropColumn(
                name: "KubernetesArtifactsImportedAt",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropColumn(
                name: "RpmNevra",
                schema: "build",
                table: "build_artifacts");

            migrationBuilder.DropColumn(
                name: "SourceStoragePath",
                schema: "build",
                table: "build_artifacts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_build_jobs_executor_identity",
                schema: "build",
                table: "build_jobs",
                sql: "(\"ExecutionBackend\" = 0 AND \"KubernetesNamespace\" IS NULL AND \"KubernetesJobName\" IS NULL AND \"KubernetesJobUid\" IS NULL AND \"KubernetesPodName\" IS NULL) OR (\"ExecutionBackend\" = 1 AND ((\"KubernetesNamespace\" IS NULL AND \"KubernetesJobName\" IS NULL AND \"KubernetesJobUid\" IS NULL AND \"KubernetesPodName\" IS NULL) OR (\"KubernetesNamespace\" IS NOT NULL AND \"KubernetesJobName\" IS NOT NULL)))");

            migrationBuilder.CreateIndex(
                name: "IX_build_artifacts_BuildJobId",
                schema: "build",
                table: "build_artifacts",
                column: "BuildJobId");
        }
    }
}
