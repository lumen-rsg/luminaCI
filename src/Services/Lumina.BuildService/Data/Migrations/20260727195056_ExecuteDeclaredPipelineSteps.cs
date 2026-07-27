using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.BuildService.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExecuteDeclaredPipelineSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                schema: "build",
                table: "pipeline_steps");

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAt",
                schema: "build",
                table: "build_artifacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PublishedRepositoryId",
                schema: "build",
                table: "build_artifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "build_step_runs",
                schema: "build",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineStepId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Configuration = table.Column<Dictionary<string, string>>(type: "hstore", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_build_step_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_build_step_runs_build_jobs_BuildJobId",
                        column: x => x.BuildJobId,
                        principalSchema: "build",
                        principalTable: "build_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_build_step_runs_BuildJobId_Order",
                schema: "build",
                table: "build_step_runs",
                columns: new[] { "BuildJobId", "Order" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "build_step_runs",
                schema: "build");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                schema: "build",
                table: "build_artifacts");

            migrationBuilder.DropColumn(
                name: "PublishedRepositoryId",
                schema: "build",
                table: "build_artifacts");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                schema: "build",
                table: "pipeline_steps",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
