using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Notes
{
    /// <inheritdoc />
    public partial class NotesInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notes");

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "notes",
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

            migrationBuilder.CreateTable(
                name: "links",
                schema: "notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    target = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    normalized_target = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    heading = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    alias = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    embed = table.Column<bool>(type: "boolean", nullable: false),
                    target_item_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_links", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notes",
                schema: "notes",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    normalized_title = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notes", x => x.item_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entity_id",
                schema: "notes",
                table: "audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id",
                schema: "notes",
                table: "audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id_at",
                schema: "notes",
                table: "audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_links_tenant_id",
                schema: "notes",
                table: "links",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_links_tenant_id_source_item_id",
                schema: "notes",
                table: "links",
                columns: new[] { "tenant_id", "source_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_links_tenant_id_target_item_id",
                schema: "notes",
                table: "links",
                columns: new[] { "tenant_id", "target_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_links_tenant_id_workspace_id_normalized_target",
                schema: "notes",
                table: "links",
                columns: new[] { "tenant_id", "workspace_id", "normalized_target" });

            migrationBuilder.CreateIndex(
                name: "ix_notes_tenant_id",
                schema: "notes",
                table: "notes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_notes_tenant_id_workspace_id_normalized_title",
                schema: "notes",
                table: "notes",
                columns: new[] { "tenant_id", "workspace_id", "normalized_title" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "notes");

            migrationBuilder.DropTable(
                name: "links",
                schema: "notes");

            migrationBuilder.DropTable(
                name: "notes",
                schema: "notes");
        }
    }
}
