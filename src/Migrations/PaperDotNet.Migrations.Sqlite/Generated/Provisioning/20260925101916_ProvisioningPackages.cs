using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Provisioning
{
    /// <inheritdoc />
    public partial class ProvisioningPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provisioning_audit_log",
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
                    table.PrimaryKey("pk_provisioning_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provisioning_packages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    operation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    blob_key = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    size = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provisioning_packages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_audit_log_entity_id",
                table: "provisioning_audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_audit_log_tenant_id",
                table: "provisioning_audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_audit_log_tenant_id_at",
                table: "provisioning_audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_packages_expires_at",
                table: "provisioning_packages",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_packages_tenant_id",
                table: "provisioning_packages",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_packages_tenant_id_created_by_created_at",
                table: "provisioning_packages",
                columns: new[] { "tenant_id", "created_by", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provisioning_audit_log");

            migrationBuilder.DropTable(
                name: "provisioning_packages");
        }
    }
}
