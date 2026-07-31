using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.BuildService.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExecuteNativePromotionGates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "native_promotion_gates",
                schema: "build",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectWebhookDeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    PromotionGroup = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TargetArchitecture = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RunnerImageDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    CandidateManifestJson = table.Column<string>(type: "jsonb", nullable: false),
                    CandidateManifestSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    KubernetesNamespace = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    KubernetesJobName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    KubernetesJobUid = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    KubernetesPodName = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    ResultSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Logs = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_native_promotion_gates", x => x.Id);
                    table.CheckConstraint("CK_native_promotion_gates_state", "(\"Status\" = 0 AND \"KubernetesNamespace\" IS NULL AND \"KubernetesJobName\" IS NULL AND \"KubernetesJobUid\" IS NULL AND \"KubernetesPodName\" IS NULL AND \"ResultSha256\" IS NULL AND \"FailureReason\" IS NULL AND \"StartedAt\" IS NULL AND \"CompletedAt\" IS NULL) OR (\"Status\" = 1 AND \"KubernetesNamespace\" IS NOT NULL AND \"KubernetesJobName\" IS NOT NULL AND \"KubernetesJobUid\" IS NOT NULL AND \"ResultSha256\" IS NULL AND \"FailureReason\" IS NULL AND \"StartedAt\" IS NOT NULL AND \"CompletedAt\" IS NULL) OR (\"Status\" = 2 AND \"KubernetesNamespace\" IS NOT NULL AND \"KubernetesJobName\" IS NOT NULL AND \"KubernetesJobUid\" IS NOT NULL AND \"ResultSha256\" IS NOT NULL AND \"FailureReason\" IS NULL AND \"StartedAt\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL) OR (\"Status\" = 3 AND \"KubernetesNamespace\" IS NOT NULL AND \"KubernetesJobName\" IS NOT NULL AND \"KubernetesJobUid\" IS NOT NULL AND \"ResultSha256\" IS NULL AND \"FailureReason\" IS NOT NULL AND \"StartedAt\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_native_promotion_gates_project_webhook_deliveries_ProjectWe~",
                        column: x => x.ProjectWebhookDeliveryId,
                        principalSchema: "build",
                        principalTable: "project_webhook_deliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_native_promotion_gates_KubernetesNamespace_KubernetesJobName",
                schema: "build",
                table: "native_promotion_gates",
                columns: new[] { "KubernetesNamespace", "KubernetesJobName" },
                unique: true,
                filter: "\"KubernetesNamespace\" IS NOT NULL AND \"KubernetesJobName\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_native_promotion_gates_ProjectWebhookDeliveryId",
                schema: "build",
                table: "native_promotion_gates",
                column: "ProjectWebhookDeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_native_promotion_gates_Status_UpdatedAt",
                schema: "build",
                table: "native_promotion_gates",
                columns: new[] { "Status", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "native_promotion_gates",
                schema: "build");
        }
    }
}
