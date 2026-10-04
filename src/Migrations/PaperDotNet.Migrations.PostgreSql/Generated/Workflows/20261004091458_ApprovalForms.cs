using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Workflows
{
    /// <inheritdoc />
    public partial class ApprovalForms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "list_id",
                schema: "automation",
                table: "approvals",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "item_id",
                schema: "automation",
                table: "approvals",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "input_schema",
                schema: "automation",
                table: "approvals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "inputs",
                schema: "automation",
                table: "approvals",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "input_schema",
                schema: "automation",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "inputs",
                schema: "automation",
                table: "approvals");

            migrationBuilder.AlterColumn<Guid>(
                name: "list_id",
                schema: "automation",
                table: "approvals",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "item_id",
                schema: "automation",
                table: "approvals",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
