using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Samples.Invoices.Migrations.Sqlite.Generated;

/// <inheritdoc />
public partial class _20261001050157_Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ext_samples_invoices_approvals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                Comment = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ext_samples_invoices_approvals", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ext_samples_invoices_approvals_TenantId_ItemId",
            table: "ext_samples_invoices_approvals",
            columns: new[] { "TenantId", "ItemId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ext_samples_invoices_approvals");
    }
}
