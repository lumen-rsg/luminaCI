using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.BuildService.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordBuildTargetIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BuildProfile",
                schema: "build",
                table: "pipelines",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "fedora-44-aarch64");

            migrationBuilder.AddColumn<string>(
                name: "TargetArchitecture",
                schema: "build",
                table: "pipelines",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "aarch64");

            migrationBuilder.AddColumn<string>(
                name: "TargetDistribution",
                schema: "build",
                table: "pipelines",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "fedora");

            migrationBuilder.AddColumn<string>(
                name: "TargetRelease",
                schema: "build",
                table: "pipelines",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "44");

            migrationBuilder.AddColumn<string>(
                name: "BuildProfile",
                schema: "build",
                table: "build_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "legacy-unrecorded");

            migrationBuilder.AddColumn<string>(
                name: "RunnerImageDigest",
                schema: "build",
                table: "build_jobs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RunnerImageReference",
                schema: "build",
                table: "build_jobs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetArchitecture",
                schema: "build",
                table: "build_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "unknown");

            migrationBuilder.AddColumn<string>(
                name: "TargetDistribution",
                schema: "build",
                table: "build_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "unknown");

            migrationBuilder.AddColumn<string>(
                name: "TargetRelease",
                schema: "build",
                table: "build_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "unknown");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BuildProfile",
                schema: "build",
                table: "pipelines");

            migrationBuilder.DropColumn(
                name: "TargetArchitecture",
                schema: "build",
                table: "pipelines");

            migrationBuilder.DropColumn(
                name: "TargetDistribution",
                schema: "build",
                table: "pipelines");

            migrationBuilder.DropColumn(
                name: "TargetRelease",
                schema: "build",
                table: "pipelines");

            migrationBuilder.DropColumn(
                name: "BuildProfile",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropColumn(
                name: "RunnerImageDigest",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropColumn(
                name: "RunnerImageReference",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropColumn(
                name: "TargetArchitecture",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropColumn(
                name: "TargetDistribution",
                schema: "build",
                table: "build_jobs");

            migrationBuilder.DropColumn(
                name: "TargetRelease",
                schema: "build",
                table: "build_jobs");
        }
    }
}
