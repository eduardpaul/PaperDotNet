using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Collaboration
{
    /// <inheritdoc />
    public partial class CollaborationInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "collaboration");

            migrationBuilder.CreateTable(
                name: "activity",
                schema: "collaboration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    changed_fields = table.Column<List<string>>(type: "text[]", nullable: false),
                    deduplication_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_activity", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "collaboration",
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
                name: "comments",
                schema: "collaboration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    mentions = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comments", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_activity_tenant_id",
                schema: "collaboration",
                table: "activity",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_activity_tenant_id_deduplication_key",
                schema: "collaboration",
                table: "activity",
                columns: new[] { "tenant_id", "deduplication_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_activity_tenant_id_item_id_at",
                schema: "collaboration",
                table: "activity",
                columns: new[] { "tenant_id", "item_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entity_id",
                schema: "collaboration",
                table: "audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id",
                schema: "collaboration",
                table: "audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id_at",
                schema: "collaboration",
                table: "audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_comments_parent_id",
                schema: "collaboration",
                table: "comments",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_comments_tenant_id",
                schema: "collaboration",
                table: "comments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_comments_tenant_id_item_id",
                schema: "collaboration",
                table: "comments",
                columns: new[] { "tenant_id", "item_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity",
                schema: "collaboration");

            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "collaboration");

            migrationBuilder.DropTable(
                name: "comments",
                schema: "collaboration");
        }
    }
}
