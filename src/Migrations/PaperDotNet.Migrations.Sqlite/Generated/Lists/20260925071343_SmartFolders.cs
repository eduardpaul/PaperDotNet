using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Lists
{
    /// <inheritdoc />
    public partial class SmartFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lists_smart_folders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    owner_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    definition = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lists_smart_folders", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lists_smart_folders_tenant_id",
                table: "lists_smart_folders",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_lists_smart_folders_tenant_id_owner_id",
                table: "lists_smart_folders",
                columns: new[] { "tenant_id", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "ix_lists_smart_folders_tenant_id_workspace_id",
                table: "lists_smart_folders",
                columns: new[] { "tenant_id", "workspace_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lists_smart_folders");
        }
    }
}
