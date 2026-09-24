using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Samples.Invoices.Migrations.PostgreSql.Generated
{
    /// <inheritdoc />
    public partial class InitialApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ext_samples_invoices");

            migrationBuilder.CreateTable(
                name: "approvals",
                schema: "ext_samples_invoices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approvals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "ext_samples_invoices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    properties = table.Column<List<string>>(type: "text[]", nullable: false),
                    trace_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_approvals_item_id",
                schema: "ext_samples_invoices",
                table: "approvals",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_tenant_id",
                schema: "ext_samples_invoices",
                table: "approvals",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entity_id",
                schema: "ext_samples_invoices",
                table: "audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id",
                schema: "ext_samples_invoices",
                table: "audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id_at",
                schema: "ext_samples_invoices",
                table: "audit_log",
                columns: new[] { "tenant_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approvals",
                schema: "ext_samples_invoices");

            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "ext_samples_invoices");
        }
    }
}
