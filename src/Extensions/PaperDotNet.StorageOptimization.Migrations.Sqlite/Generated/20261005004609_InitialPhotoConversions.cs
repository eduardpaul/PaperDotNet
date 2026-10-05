using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.StorageOptimization.Migrations.Sqlite.Generated
{
    /// <inheritdoc />
    public partial class InitialPhotoConversions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ext_paperdotnet_storageoptimization_audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    at = table.Column<long>(type: "INTEGER", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    action = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    entity_type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    entity_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    properties = table.Column<string>(type: "TEXT", nullable: false),
                    trace_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ext_paperdotnet_storageoptimization_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ext_paperdotnet_storageoptimization_photo_conversions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    primary_item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    started_by = table.Column<Guid>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    file_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    languages = table.Column<string>(type: "TEXT", nullable: false),
                    sources_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ext_paperdotnet_storageoptimization_photo_conversions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ext_paperdotnet_storageoptimization_audit_log_entity_id",
                table: "ext_paperdotnet_storageoptimization_audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_ext_paperdotnet_storageoptimization_audit_log_tenant_id",
                table: "ext_paperdotnet_storageoptimization_audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_ext_paperdotnet_storageoptimization_audit_log_tenant_id_at",
                table: "ext_paperdotnet_storageoptimization_audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_ext_paperdotnet_storageoptimization_photo_conversions_run_id",
                table: "ext_paperdotnet_storageoptimization_photo_conversions",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_ext_paperdotnet_storageoptimization_photo_conversions_tenant_id",
                table: "ext_paperdotnet_storageoptimization_photo_conversions",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ext_paperdotnet_storageoptimization_audit_log");

            migrationBuilder.DropTable(
                name: "ext_paperdotnet_storageoptimization_photo_conversions");
        }
    }
}
