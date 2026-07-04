using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.BuildService.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialBuildSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "build");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:hstore", ",,");

            migrationBuilder.CreateTable(
                name: "pipelines",
                schema: "build",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    GitRepoUrl = table.Column<string>(type: "text", nullable: true),
                    GitBranch = table.Column<string>(type: "text", nullable: true),
                    SpecPath = table.Column<string>(type: "text", nullable: true),
                    WebhookSecret = table.Column<string>(type: "text", nullable: true),
                    GitUsername = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    GitToken = table.Column<string>(type: "text", nullable: true),
                    BuildImage = table.Column<string>(type: "text", nullable: true),
                    SpecContent = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pipelines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "build_jobs",
                schema: "build",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SpecName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SpecContent = table.Column<string>(type: "text", nullable: false),
                    SourceUrl = table.Column<string>(type: "text", nullable: false),
                    ContainerId = table.Column<string>(type: "text", nullable: true),
                    Logs = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TriggeredBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Branch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CommitMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CommitAuthor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_build_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_build_jobs_pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalSchema: "build",
                        principalTable: "pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pipeline_steps",
                schema: "build",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Configuration = table.Column<Dictionary<string, string>>(type: "hstore", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pipeline_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pipeline_steps_pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalSchema: "build",
                        principalTable: "pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "build_artifacts",
                schema: "build",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    FilePath = table.Column<string>(type: "text", nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    HashSha256 = table.Column<string>(type: "text", nullable: true),
                    HashSha512 = table.Column<string>(type: "text", nullable: true),
                    HashMd5 = table.Column<string>(type: "text", nullable: true),
                    PgpSignature = table.Column<string>(type: "text", nullable: true),
                    CveScanStatus = table.Column<int>(type: "integer", nullable: false),
                    StoragePath = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_build_artifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_build_artifacts_build_jobs_BuildJobId",
                        column: x => x.BuildJobId,
                        principalSchema: "build",
                        principalTable: "build_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_build_artifacts_BuildJobId",
                schema: "build",
                table: "build_artifacts",
                column: "BuildJobId");

            migrationBuilder.CreateIndex(
                name: "IX_build_jobs_PipelineId",
                schema: "build",
                table: "build_jobs",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_pipeline_steps_PipelineId",
                schema: "build",
                table: "pipeline_steps",
                column: "PipelineId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "build_artifacts",
                schema: "build");

            migrationBuilder.DropTable(
                name: "pipeline_steps",
                schema: "build");

            migrationBuilder.DropTable(
                name: "build_jobs",
                schema: "build");

            migrationBuilder.DropTable(
                name: "pipelines",
                schema: "build");
        }
    }
}
