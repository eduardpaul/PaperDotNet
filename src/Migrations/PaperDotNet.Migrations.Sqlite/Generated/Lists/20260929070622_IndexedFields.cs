using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Lists
{
    /// <inheritdoc />
    public partial class IndexedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "index_pending",
                table: "lists_lists",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "indexed_fields",
                table: "lists_lists",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<short>(
                name: "next_value_field",
                table: "lists_lists",
                type: "INTEGER",
                nullable: false,
                defaultValue: (short)16);

            migrationBuilder.AddColumn<string>(
                name: "date1",
                table: "lists_items",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "date10",
                table: "lists_items",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "date2",
                table: "lists_items",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "date3",
                table: "lists_items",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "date4",
                table: "lists_items",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "date5",
                table: "lists_items",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "date6",
                table: "lists_items",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "date7",
                table: "lists_items",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "date8",
                table: "lists_items",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "date9",
                table: "lists_items",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "number1",
                table: "lists_items",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "number10",
                table: "lists_items",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "number2",
                table: "lists_items",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "number3",
                table: "lists_items",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "number4",
                table: "lists_items",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "number5",
                table: "lists_items",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "number6",
                table: "lists_items",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "number7",
                table: "lists_items",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "number8",
                table: "lists_items",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "number9",
                table: "lists_items",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "text1",
                table: "lists_items",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "text10",
                table: "lists_items",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "text2",
                table: "lists_items",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "text3",
                table: "lists_items",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "text4",
                table: "lists_items",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "text5",
                table: "lists_items",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "text6",
                table: "lists_items",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "text7",
                table: "lists_items",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "text8",
                table: "lists_items",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "text9",
                table: "lists_items",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "lists_item_values",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    field = table.Column<short>(type: "INTEGER", nullable: false),
                    value = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lists_item_values", x => new { x.item_id, x.field, x.value });
                });

            migrationBuilder.CreateIndex(
                name: "ix_lists_lists_tenant_id_index_pending",
                table: "lists_lists",
                columns: new[] { "tenant_id", "index_pending" });

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_date1_id",
                table: "lists_items",
                columns: new[] { "list_id", "date1", "id" },
                filter: "date1 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_date10_id",
                table: "lists_items",
                columns: new[] { "list_id", "date10", "id" },
                filter: "date10 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_date2_id",
                table: "lists_items",
                columns: new[] { "list_id", "date2", "id" },
                filter: "date2 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_date3_id",
                table: "lists_items",
                columns: new[] { "list_id", "date3", "id" },
                filter: "date3 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_date4_id",
                table: "lists_items",
                columns: new[] { "list_id", "date4", "id" },
                filter: "date4 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_date5_id",
                table: "lists_items",
                columns: new[] { "list_id", "date5", "id" },
                filter: "date5 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_date6_id",
                table: "lists_items",
                columns: new[] { "list_id", "date6", "id" },
                filter: "date6 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_date7_id",
                table: "lists_items",
                columns: new[] { "list_id", "date7", "id" },
                filter: "date7 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_date8_id",
                table: "lists_items",
                columns: new[] { "list_id", "date8", "id" },
                filter: "date8 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_date9_id",
                table: "lists_items",
                columns: new[] { "list_id", "date9", "id" },
                filter: "date9 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_number1_id",
                table: "lists_items",
                columns: new[] { "list_id", "number1", "id" },
                filter: "number1 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_number10_id",
                table: "lists_items",
                columns: new[] { "list_id", "number10", "id" },
                filter: "number10 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_number2_id",
                table: "lists_items",
                columns: new[] { "list_id", "number2", "id" },
                filter: "number2 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_number3_id",
                table: "lists_items",
                columns: new[] { "list_id", "number3", "id" },
                filter: "number3 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_number4_id",
                table: "lists_items",
                columns: new[] { "list_id", "number4", "id" },
                filter: "number4 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_number5_id",
                table: "lists_items",
                columns: new[] { "list_id", "number5", "id" },
                filter: "number5 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_number6_id",
                table: "lists_items",
                columns: new[] { "list_id", "number6", "id" },
                filter: "number6 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_number7_id",
                table: "lists_items",
                columns: new[] { "list_id", "number7", "id" },
                filter: "number7 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_number8_id",
                table: "lists_items",
                columns: new[] { "list_id", "number8", "id" },
                filter: "number8 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_number9_id",
                table: "lists_items",
                columns: new[] { "list_id", "number9", "id" },
                filter: "number9 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_parent_id_is_folder_title_id",
                table: "lists_items",
                columns: new[] { "list_id", "parent_id", "is_folder", "title", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_text1_id",
                table: "lists_items",
                columns: new[] { "list_id", "text1", "id" },
                filter: "text1 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_text10_id",
                table: "lists_items",
                columns: new[] { "list_id", "text10", "id" },
                filter: "text10 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_text2_id",
                table: "lists_items",
                columns: new[] { "list_id", "text2", "id" },
                filter: "text2 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_text3_id",
                table: "lists_items",
                columns: new[] { "list_id", "text3", "id" },
                filter: "text3 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_text4_id",
                table: "lists_items",
                columns: new[] { "list_id", "text4", "id" },
                filter: "text4 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_text5_id",
                table: "lists_items",
                columns: new[] { "list_id", "text5", "id" },
                filter: "text5 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_text6_id",
                table: "lists_items",
                columns: new[] { "list_id", "text6", "id" },
                filter: "text6 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_text7_id",
                table: "lists_items",
                columns: new[] { "list_id", "text7", "id" },
                filter: "text7 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_text8_id",
                table: "lists_items",
                columns: new[] { "list_id", "text8", "id" },
                filter: "text8 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_items_list_id_text9_id",
                table: "lists_items",
                columns: new[] { "list_id", "text9", "id" },
                filter: "text9 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lists_item_values_list_id_field_value_item_id",
                table: "lists_item_values",
                columns: new[] { "list_id", "field", "value", "item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_lists_item_values_tenant_id",
                table: "lists_item_values",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lists_item_values");

            migrationBuilder.DropIndex(
                name: "ix_lists_lists_tenant_id_index_pending",
                table: "lists_lists");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_date1_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_date10_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_date2_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_date3_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_date4_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_date5_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_date6_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_date7_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_date8_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_date9_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_number1_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_number10_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_number2_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_number3_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_number4_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_number5_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_number6_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_number7_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_number8_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_number9_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_parent_id_is_folder_title_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_text1_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_text10_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_text2_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_text3_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_text4_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_text5_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_text6_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_text7_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_text8_id",
                table: "lists_items");

            migrationBuilder.DropIndex(
                name: "ix_lists_items_list_id_text9_id",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "index_pending",
                table: "lists_lists");

            migrationBuilder.DropColumn(
                name: "indexed_fields",
                table: "lists_lists");

            migrationBuilder.DropColumn(
                name: "next_value_field",
                table: "lists_lists");

            migrationBuilder.DropColumn(
                name: "date1",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "date10",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "date2",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "date3",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "date4",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "date5",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "date6",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "date7",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "date8",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "date9",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "number1",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "number10",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "number2",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "number3",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "number4",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "number5",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "number6",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "number7",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "number8",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "number9",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "text1",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "text10",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "text2",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "text3",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "text4",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "text5",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "text6",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "text7",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "text8",
                table: "lists_items");

            migrationBuilder.DropColumn(
                name: "text9",
                table: "lists_items");
        }
    }
}
