using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Notes
{
    /// <inheritdoc />
    public partial class NotesInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notes_audit_log",
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
                    table.PrimaryKey("pk_notes_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notes_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    source_item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    source_list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    target = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    normalized_target = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    heading = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    alias = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    embed = table.Column<bool>(type: "INTEGER", nullable: false),
                    target_item_id = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notes_links", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notes_notes",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    normalized_title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notes_notes", x => x.item_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notes_audit_log_entity_id",
                table: "notes_audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_notes_audit_log_tenant_id",
                table: "notes_audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_notes_audit_log_tenant_id_at",
                table: "notes_audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_notes_links_tenant_id",
                table: "notes_links",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_notes_links_tenant_id_source_item_id",
                table: "notes_links",
                columns: new[] { "tenant_id", "source_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_notes_links_tenant_id_target_item_id",
                table: "notes_links",
                columns: new[] { "tenant_id", "target_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_notes_links_tenant_id_workspace_id_normalized_target",
                table: "notes_links",
                columns: new[] { "tenant_id", "workspace_id", "normalized_target" });

            migrationBuilder.CreateIndex(
                name: "ix_notes_notes_tenant_id",
                table: "notes_notes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_notes_notes_tenant_id_workspace_id_normalized_title",
                table: "notes_notes",
                columns: new[] { "tenant_id", "workspace_id", "normalized_title" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notes_audit_log");

            migrationBuilder.DropTable(
                name: "notes_links");

            migrationBuilder.DropTable(
                name: "notes_notes");
        }
    }
}
