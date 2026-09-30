using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Lists;

/// <inheritdoc />
public partial class _20260930220853_ListViews : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "list_views",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Columns = table.Column<string>(type: "TEXT", nullable: false),
                Filter = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                OrderBy = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                GroupBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                Layout = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_list_views", x => x.Id);
                table.ForeignKey(
                    name: "FK_list_views_lists_ListId",
                    column: x => x.ListId,
                    principalTable: "lists",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_list_views_ListId",
            table: "list_views",
            column: "ListId");

        migrationBuilder.CreateIndex(
            name: "IX_list_views_TenantId_ListId",
            table: "list_views",
            columns: new[] { "TenantId", "ListId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "list_views");
    }
}
