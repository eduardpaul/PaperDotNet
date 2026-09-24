using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Extensions
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "extensions_audit_log",
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
                    table.PrimaryKey("pk_extensions_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "extensions_tenant_extensions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    extension_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    settings = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_extensions_tenant_extensions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_extensions_audit_log_entity_id",
                table: "extensions_audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_extensions_audit_log_tenant_id",
                table: "extensions_audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_extensions_audit_log_tenant_id_at",
                table: "extensions_audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_extensions_tenant_extensions_tenant_id",
                table: "extensions_tenant_extensions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_extensions_tenant_extensions_tenant_id_extension_id",
                table: "extensions_tenant_extensions",
                columns: new[] { "tenant_id", "extension_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "extensions_audit_log");

            migrationBuilder.DropTable(
                name: "extensions_tenant_extensions");
        }
    }
}
