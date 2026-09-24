using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workspaces
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workspaces_workspaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspaces_workspaces", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workspaces_members",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    role = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspaces_members", x => new { x.workspace_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_workspaces_members_workspaces_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_workspaces_members_tenant_id",
                table: "workspaces_members",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_workspaces_members_user_id",
                table: "workspaces_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_workspaces_workspaces_tenant_id",
                table: "workspaces_workspaces",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workspaces_members");

            migrationBuilder.DropTable(
                name: "workspaces_workspaces");
        }
    }
}
