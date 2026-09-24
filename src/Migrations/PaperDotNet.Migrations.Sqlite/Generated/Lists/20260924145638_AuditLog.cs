using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Lists
{
    /// <inheritdoc />
    public partial class AuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "max_versions",
                table: "lists_lists",
                type: "INTEGER",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.AddColumn<string>(
                name: "versioning",
                table: "lists_lists",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Off");

            migrationBuilder.CreateTable(
                name: "lists_audit_log",
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
                    table.PrimaryKey("pk_lists_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "lists_item_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    number = table.Column<int>(type: "INTEGER", nullable: false),
                    content_type_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    fields = table.Column<string>(type: "TEXT", nullable: false),
                    changed_fields = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lists_item_versions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lists_audit_log_entity_id",
                table: "lists_audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_lists_audit_log_tenant_id",
                table: "lists_audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_lists_audit_log_tenant_id_at",
                table: "lists_audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_lists_item_versions_item_id_number",
                table: "lists_item_versions",
                columns: new[] { "item_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lists_item_versions_tenant_id",
                table: "lists_item_versions",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lists_audit_log");

            migrationBuilder.DropTable(
                name: "lists_item_versions");

            migrationBuilder.DropColumn(
                name: "max_versions",
                table: "lists_lists");

            migrationBuilder.DropColumn(
                name: "versioning",
                table: "lists_lists");
        }
    }
}
