using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Lists;

/// <inheritdoc />
public partial class _20260930225442_IndexedFields : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IndexPending",
            table: "lists",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "IndexedFields",
            table: "lists",
            type: "TEXT",
            nullable: false,
            defaultValue: "[]");

        migrationBuilder.AddColumn<short>(
            name: "NextValueField",
            table: "lists",
            type: "INTEGER",
            nullable: false,
            defaultValue: (short)16);

        migrationBuilder.AddColumn<string>(
            name: "Date1",
            table: "list_items",
            type: "TEXT",
            maxLength: 40,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Date10",
            table: "list_items",
            type: "TEXT",
            maxLength: 40,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Date2",
            table: "list_items",
            type: "TEXT",
            maxLength: 40,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Date3",
            table: "list_items",
            type: "TEXT",
            maxLength: 40,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Date4",
            table: "list_items",
            type: "TEXT",
            maxLength: 40,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Date5",
            table: "list_items",
            type: "TEXT",
            maxLength: 40,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Date6",
            table: "list_items",
            type: "TEXT",
            maxLength: 40,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Date7",
            table: "list_items",
            type: "TEXT",
            maxLength: 40,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Date8",
            table: "list_items",
            type: "TEXT",
            maxLength: 40,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Date9",
            table: "list_items",
            type: "TEXT",
            maxLength: 40,
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "Number1",
            table: "list_items",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "Number10",
            table: "list_items",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "Number2",
            table: "list_items",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "Number3",
            table: "list_items",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "Number4",
            table: "list_items",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "Number5",
            table: "list_items",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "Number6",
            table: "list_items",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "Number7",
            table: "list_items",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "Number8",
            table: "list_items",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "Number9",
            table: "list_items",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Text1",
            table: "list_items",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Text10",
            table: "list_items",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Text2",
            table: "list_items",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Text3",
            table: "list_items",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Text4",
            table: "list_items",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Text5",
            table: "list_items",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Text6",
            table: "list_items",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Text7",
            table: "list_items",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Text8",
            table: "list_items",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Text9",
            table: "list_items",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "item_values",
            columns: table => new
            {
                ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                Field = table.Column<short>(type: "INTEGER", nullable: false),
                Value = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_item_values", x => new { x.ItemId, x.Field, x.Value });
            });

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Date10_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Date10", "Id" },
            filter: "\"Date10\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Date1_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Date1", "Id" },
            filter: "\"Date1\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Date2_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Date2", "Id" },
            filter: "\"Date2\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Date3_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Date3", "Id" },
            filter: "\"Date3\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Date4_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Date4", "Id" },
            filter: "\"Date4\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Date5_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Date5", "Id" },
            filter: "\"Date5\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Date6_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Date6", "Id" },
            filter: "\"Date6\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Date7_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Date7", "Id" },
            filter: "\"Date7\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Date8_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Date8", "Id" },
            filter: "\"Date8\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Date9_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Date9", "Id" },
            filter: "\"Date9\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Number10_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Number10", "Id" },
            filter: "\"Number10\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Number1_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Number1", "Id" },
            filter: "\"Number1\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Number2_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Number2", "Id" },
            filter: "\"Number2\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Number3_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Number3", "Id" },
            filter: "\"Number3\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Number4_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Number4", "Id" },
            filter: "\"Number4\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Number5_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Number5", "Id" },
            filter: "\"Number5\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Number6_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Number6", "Id" },
            filter: "\"Number6\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Number7_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Number7", "Id" },
            filter: "\"Number7\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Number8_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Number8", "Id" },
            filter: "\"Number8\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Number9_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Number9", "Id" },
            filter: "\"Number9\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Text10_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Text10", "Id" },
            filter: "\"Text10\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Text1_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Text1", "Id" },
            filter: "\"Text1\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Text2_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Text2", "Id" },
            filter: "\"Text2\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Text3_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Text3", "Id" },
            filter: "\"Text3\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Text4_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Text4", "Id" },
            filter: "\"Text4\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Text5_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Text5", "Id" },
            filter: "\"Text5\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Text6_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Text6", "Id" },
            filter: "\"Text6\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Text7_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Text7", "Id" },
            filter: "\"Text7\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Text8_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Text8", "Id" },
            filter: "\"Text8\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_list_items_TenantId_ListId_Text9_Id",
            table: "list_items",
            columns: new[] { "TenantId", "ListId", "Text9", "Id" },
            filter: "\"Text9\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_item_values_TenantId_ListId_Field_Value_ItemId",
            table: "item_values",
            columns: new[] { "TenantId", "ListId", "Field", "Value", "ItemId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "item_values");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Date10_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Date1_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Date2_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Date3_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Date4_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Date5_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Date6_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Date7_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Date8_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Date9_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Number10_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Number1_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Number2_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Number3_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Number4_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Number5_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Number6_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Number7_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Number8_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Number9_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Text10_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Text1_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Text2_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Text3_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Text4_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Text5_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Text6_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Text7_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Text8_Id",
            table: "list_items");

        migrationBuilder.DropIndex(
            name: "IX_list_items_TenantId_ListId_Text9_Id",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "IndexPending",
            table: "lists");

        migrationBuilder.DropColumn(
            name: "IndexedFields",
            table: "lists");

        migrationBuilder.DropColumn(
            name: "NextValueField",
            table: "lists");

        migrationBuilder.DropColumn(
            name: "Date1",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Date10",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Date2",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Date3",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Date4",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Date5",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Date6",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Date7",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Date8",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Date9",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Number1",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Number10",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Number2",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Number3",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Number4",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Number5",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Number6",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Number7",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Number8",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Number9",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Text1",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Text10",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Text2",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Text3",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Text4",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Text5",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Text6",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Text7",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Text8",
            table: "list_items");

        migrationBuilder.DropColumn(
            name: "Text9",
            table: "list_items");
    }
}
