using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Lists
{
    /// <inheritdoc />
    public partial class ItemChangeLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "item_changes",
                schema: "lists",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_changes", x => x.sequence);
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_changes_list_id_sequence",
                schema: "lists",
                table: "item_changes",
                columns: new[] { "list_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_item_changes_tenant_id",
                schema: "lists",
                table: "item_changes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_item_changes_tenant_id_at",
                schema: "lists",
                table: "item_changes",
                columns: new[] { "tenant_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "item_changes",
                schema: "lists");
        }
    }
}
