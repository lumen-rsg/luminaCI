using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Lumina.ApiGateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTamperEvidentAuditLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "audit_logs",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    Action = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    PerformedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Details = table.Column<string>(type: "text", nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Phase = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    PreviousHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntryHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.Id);
                    table.CheckConstraint("CK_audit_logs_Details", "\"Details\" IS JSON OBJECT");
                    table.CheckConstraint("CK_audit_logs_EntryHash", "\"EntryHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_audit_logs_Phase", "\"Phase\" IN ('Requested', 'Completed', 'Failed')");
                    table.CheckConstraint("CK_audit_logs_PreviousHash", "\"PreviousHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_audit_logs_StatusCode", "\"StatusCode\" IS NULL OR \"StatusCode\" BETWEEN 100 AND 599");
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_CorrelationId",
                schema: "audit",
                table: "audit_logs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_EntityType_EntityId",
                schema: "audit",
                table: "audit_logs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_EntryHash",
                schema: "audit",
                table: "audit_logs",
                column: "EntryHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_PerformedBy",
                schema: "audit",
                table: "audit_logs",
                column: "PerformedBy");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_Sequence",
                schema: "audit",
                table: "audit_logs",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_Timestamp",
                schema: "audit",
                table: "audit_logs",
                column: "Timestamp");

            migrationBuilder.Sql(
                """
                CREATE FUNCTION audit.reject_audit_log_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'audit ledger rows are append-only'
                        USING ERRCODE = '55000';
                END;
                $$;

                CREATE TRIGGER audit_logs_are_append_only
                BEFORE UPDATE OR DELETE OR TRUNCATE
                ON audit.audit_logs
                FOR EACH STATEMENT
                EXECUTE FUNCTION audit.reject_audit_log_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_logs",
                schema: "audit");

            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS audit.reject_audit_log_mutation();");
        }
    }
}
