using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Collaboration
{
    /// <inheritdoc />
    public partial class CollaborationInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "collaboration_activity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    actor_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    summary = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    changed_fields = table.Column<string>(type: "TEXT", nullable: false),
                    deduplication_key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collaboration_activity", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "collaboration_audit_log",
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
                    table.PrimaryKey("pk_collaboration_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "collaboration_comments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    parent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    text = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    mentions = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collaboration_comments", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_collaboration_activity_tenant_id",
                table: "collaboration_activity",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_collaboration_activity_tenant_id_deduplication_key",
                table: "collaboration_activity",
                columns: new[] { "tenant_id", "deduplication_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_collaboration_activity_tenant_id_item_id_at",
                table: "collaboration_activity",
                columns: new[] { "tenant_id", "item_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_collaboration_audit_log_entity_id",
                table: "collaboration_audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_collaboration_audit_log_tenant_id",
                table: "collaboration_audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_collaboration_audit_log_tenant_id_at",
                table: "collaboration_audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_collaboration_comments_parent_id",
                table: "collaboration_comments",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_collaboration_comments_tenant_id",
                table: "collaboration_comments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_collaboration_comments_tenant_id_item_id",
                table: "collaboration_comments",
                columns: new[] { "tenant_id", "item_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "collaboration_activity");

            migrationBuilder.DropTable(
                name: "collaboration_audit_log");

            migrationBuilder.DropTable(
                name: "collaboration_comments");
        }
    }
}
