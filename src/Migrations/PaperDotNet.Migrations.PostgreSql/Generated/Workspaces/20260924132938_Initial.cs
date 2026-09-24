using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Workspaces
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "workspaces");

            migrationBuilder.CreateTable(
                name: "workspaces",
                schema: "workspaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspaces", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "members",
                schema: "workspaces",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_members", x => new { x.workspace_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_members_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalSchema: "workspaces",
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_members_tenant_id",
                schema: "workspaces",
                table: "members",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_members_user_id",
                schema: "workspaces",
                table: "members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_workspaces_tenant_id",
                schema: "workspaces",
                table: "workspaces",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "members",
                schema: "workspaces");

            migrationBuilder.DropTable(
                name: "workspaces",
                schema: "workspaces");
        }
    }
}
