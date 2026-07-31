using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.BuildService.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordBuildExecutorIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExecutionBackend",
                schema: "build",
                table: "build_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "KubernetesJobName",
                schema: "build",
                table: "build_jobs",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KubernetesJobUid",
                schema: "build",
                table: "build_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KubernetesNamespace",
                schema: "build",
                table: "build_jobs",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KubernetesPodName",
                schema: "build",
                table: "build_jobs",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_build_jobs_ExecutionBackend_Status",
                schema: "build",
                table: "build_jobs",
                columns: new[] { "ExecutionBackend", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_build_jobs_KubernetesNamespace_KubernetesJobName",
                schema: "build",
                table: "build_jobs",
                columns: new[] { "KubernetesNamespace", "KubernetesJobName" },
                unique: true,
                filter: "\"KubernetesNamespace\" IS NOT NULL AND \"KubernetesJobName\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_build_jobs_executor_identity",
                schema: "build",
                table: "build_jobs",
                sql: "(\"ExecutionBackend\" = 0 AND \"KubernetesNamespace\" IS NULL AND \"KubernetesJobName\" IS NULL AND \"KubernetesJobUid\" IS NULL AND \"KubernetesPodName\" IS NULL) OR (\"ExecutionBackend\" = 1 AND ((\"KubernetesNamespace\" IS NULL AND \"KubernetesJobName\" IS NULL AND \"KubernetesJobUid\" IS NULL AND \"KubernetesPodName\" IS NULL) OR (\"KubernetesNamespace\" IS NOT NULL AND \"KubernetesJobName\" IS NOT NULL)))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_build_jobs_ExecutionBackend_Status",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropIndex(
                name: "IX_build_jobs_KubernetesNamespace_KubernetesJobName",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_build_jobs_executor_identity",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropColumn(
                name: "ExecutionBackend",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropColumn(
                name: "KubernetesJobName",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropColumn(
                name: "KubernetesJobUid",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropColumn(
                name: "KubernetesNamespace",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropColumn(
                name: "KubernetesPodName",
                schema: "build",
                table: "build_jobs");
        }
    }
}
