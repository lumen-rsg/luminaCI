using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.BuildService.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordRpmSigningMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PgpSignature",
                schema: "build",
                table: "build_artifacts");

            migrationBuilder.AddColumn<DateTime>(
                name: "SignedAt",
                schema: "build",
                table: "build_artifacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SigningKeyFingerprint",
                schema: "build",
                table: "build_artifacts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SignedAt",
                schema: "build",
                table: "build_artifacts");

            migrationBuilder.DropColumn(
                name: "SigningKeyFingerprint",
                schema: "build",
                table: "build_artifacts");

            migrationBuilder.AddColumn<string>(
                name: "PgpSignature",
                schema: "build",
                table: "build_artifacts",
                type: "text",
                nullable: true);
        }
    }
}
