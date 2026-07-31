using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumina.BuildService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelineTriggerPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "TriggerPaths",
                schema: "build",
                table: "pipelines",
                type: "text[]",
                nullable: false,
                defaultValue: new string[] { });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TriggerPaths",
                schema: "build",
                table: "pipelines");
        }
    }
}
