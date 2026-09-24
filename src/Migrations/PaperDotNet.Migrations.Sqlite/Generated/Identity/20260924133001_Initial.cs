using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Identity
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "identity_api_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_identifier = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    prefix = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    hash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    scopes = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_used_at = table.Column<long>(type: "INTEGER", nullable: true),
                    revoked_at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_api_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_group_members",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_group_members", x => new { x.group_id, x.user_id });
                });

            migrationBuilder.CreateTable(
                name: "identity_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_role_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    role_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    principal_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    principal_type = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_role_assignments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    is_built_in = table.Column<bool>(type: "INTEGER", nullable: false),
                    grants_all_scopes = table.Column<bool>(type: "INTEGER", nullable: false),
                    scopes = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    is_disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    user_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    normalized_user_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    normalized_email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    email_confirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", nullable: true),
                    security_stamp = table.Column<string>(type: "TEXT", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "TEXT", nullable: true),
                    phone_number = table.Column<string>(type: "TEXT", nullable: true),
                    phone_number_confirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    lockout_end = table.Column<long>(type: "INTEGER", nullable: true),
                    lockout_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    access_failed_count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_user_claims",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    claim_type = table.Column<string>(type: "TEXT", nullable: true),
                    claim_value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_user_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_identity_user_claims_users_user_id",
                        column: x => x.user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "identity_user_logins",
                columns: table => new
                {
                    login_provider = table.Column<string>(type: "TEXT", nullable: false),
                    provider_key = table.Column<string>(type: "TEXT", nullable: false),
                    provider_display_name = table.Column<string>(type: "TEXT", nullable: true),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_user_logins", x => new { x.login_provider, x.provider_key });
                    table.ForeignKey(
                        name: "fk_identity_user_logins_users_user_id",
                        column: x => x.user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "identity_user_tokens",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    login_provider = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_user_tokens", x => new { x.user_id, x.login_provider, x.name });
                    table.ForeignKey(
                        name: "fk_identity_user_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_identity_api_tokens_hash",
                table: "identity_api_tokens",
                column: "hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_identity_api_tokens_tenant_id_user_id",
                table: "identity_api_tokens",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_identity_group_members_tenant_id",
                table: "identity_group_members",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_groups_tenant_id",
                table: "identity_groups",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_groups_tenant_id_name",
                table: "identity_groups",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_identity_role_assignments_role_id_principal_id_principal_type",
                table: "identity_role_assignments",
                columns: new[] { "role_id", "principal_id", "principal_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_identity_role_assignments_tenant_id",
                table: "identity_role_assignments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_roles_tenant_id",
                table: "identity_roles",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_roles_tenant_id_name",
                table: "identity_roles",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_identity_user_claims_user_id",
                table: "identity_user_claims",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_user_logins_user_id",
                table: "identity_user_logins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "identity_users",
                column: "normalized_email");

            migrationBuilder.CreateIndex(
                name: "ix_identity_users_tenant_id",
                table: "identity_users",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_users_tenant_id_normalized_user_name",
                table: "identity_users",
                columns: new[] { "tenant_id", "normalized_user_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "identity_users",
                column: "normalized_user_name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "identity_api_tokens");

            migrationBuilder.DropTable(
                name: "identity_group_members");

            migrationBuilder.DropTable(
                name: "identity_groups");

            migrationBuilder.DropTable(
                name: "identity_role_assignments");

            migrationBuilder.DropTable(
                name: "identity_roles");

            migrationBuilder.DropTable(
                name: "identity_user_claims");

            migrationBuilder.DropTable(
                name: "identity_user_logins");

            migrationBuilder.DropTable(
                name: "identity_user_tokens");

            migrationBuilder.DropTable(
                name: "identity_users");
        }
    }
}
