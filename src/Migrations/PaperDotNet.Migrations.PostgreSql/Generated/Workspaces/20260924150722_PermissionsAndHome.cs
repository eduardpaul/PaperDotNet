using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Workspaces
{
    /// <inheritdoc />
    public partial class PermissionsAndHome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "personal_owner_id",
                schema: "workspaces",
                table: "workspaces",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_workspaces_tenant_id_personal_owner_id",
                schema: "workspaces",
                table: "workspaces",
                columns: new[] { "tenant_id", "personal_owner_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workspaces_tenant_id_personal_owner_id",
                schema: "workspaces",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "personal_owner_id",
                schema: "workspaces",
                table: "workspaces");
        }
    }
}
