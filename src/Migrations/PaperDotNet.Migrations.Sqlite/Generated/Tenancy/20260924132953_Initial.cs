using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Tenancy
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenancy_tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    identifier = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    hosts = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenancy_tenants", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenancy_tenants_identifier",
                table: "tenancy_tenants",
                column: "identifier",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenancy_tenants");
        }
    }
}
