using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Samples.Invoices.Migrations.Sqlite.Generated
{
    /// <inheritdoc />
    public partial class InitialApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ext_samples_invoices_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    comment = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ext_samples_invoices_approvals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ext_samples_invoices_audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    at = table.Column<long>(type: "INTEGER", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    action = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    entity_type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    entity_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    properties = table.Column<string>(type: "TEXT", nullable: false),
                    trace_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ext_samples_invoices_audit_log", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ext_samples_invoices_approvals_item_id",
                table: "ext_samples_invoices_approvals",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_ext_samples_invoices_approvals_tenant_id",
                table: "ext_samples_invoices_approvals",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_ext_samples_invoices_audit_log_entity_id",
                table: "ext_samples_invoices_audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_ext_samples_invoices_audit_log_tenant_id",
                table: "ext_samples_invoices_audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_ext_samples_invoices_audit_log_tenant_id_at",
                table: "ext_samples_invoices_audit_log",
                columns: new[] { "tenant_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ext_samples_invoices_approvals");

            migrationBuilder.DropTable(
                name: "ext_samples_invoices_audit_log");
        }
    }
}
