using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Lists
{
    /// <inheritdoc />
    public partial class SmartFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "smart_folders",
                schema: "lists",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    definition = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_smart_folders", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_smart_folders_tenant_id",
                schema: "lists",
                table: "smart_folders",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_smart_folders_tenant_id_owner_id",
                schema: "lists",
                table: "smart_folders",
                columns: new[] { "tenant_id", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "ix_smart_folders_tenant_id_workspace_id",
                schema: "lists",
                table: "smart_folders",
                columns: new[] { "tenant_id", "workspace_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "smart_folders",
                schema: "lists");
        }
    }
}
