using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Identity
{
    /// <inheritdoc />
    public partial class OAuthPasskeysDataProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_service_account",
                table: "identity_users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "identity_data_protection_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    friendly_name = table.Column<string>(type: "TEXT", nullable: true),
                    xml = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_oauth_applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    service_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    application_type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    client_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    client_secret = table.Column<string>(type: "TEXT", nullable: true),
                    client_type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    concurrency_token = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    consent_type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    display_name = table.Column<string>(type: "TEXT", nullable: true),
                    display_names = table.Column<string>(type: "TEXT", nullable: true),
                    json_web_key_set = table.Column<string>(type: "TEXT", nullable: true),
                    permissions = table.Column<string>(type: "TEXT", nullable: true),
                    post_logout_redirect_uris = table.Column<string>(type: "TEXT", nullable: true),
                    properties = table.Column<string>(type: "TEXT", nullable: true),
                    redirect_uris = table.Column<string>(type: "TEXT", nullable: true),
                    requirements = table.Column<string>(type: "TEXT", nullable: true),
                    settings = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_oauth_applications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_oauth_scopes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    concurrency_token = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    descriptions = table.Column<string>(type: "TEXT", nullable: true),
                    display_name = table.Column<string>(type: "TEXT", nullable: true),
                    display_names = table.Column<string>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    properties = table.Column<string>(type: "TEXT", nullable: true),
                    resources = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_oauth_scopes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_server_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    use = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    protected_key = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_server_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_user_passkeys",
                columns: table => new
                {
                    credential_id = table.Column<byte[]>(type: "BLOB", maxLength: 1024, nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    data = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_user_passkeys", x => x.credential_id);
                    table.ForeignKey(
                        name: "fk_identity_user_passkeys_users_user_id",
                        column: x => x.user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "identity_oauth_authorizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    application_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    concurrency_token = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    creation_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    properties = table.Column<string>(type: "TEXT", nullable: true),
                    scopes = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    subject = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_oauth_authorizations", x => x.id);
                    table.ForeignKey(
                        name: "fk_identity_oauth_authorizations_identity_oauth_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "identity_oauth_applications",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "identity_oauth_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    application_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    authorization_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    concurrency_token = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    creation_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    expiration_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    payload = table.Column<string>(type: "TEXT", nullable: true),
                    properties = table.Column<string>(type: "TEXT", nullable: true),
                    redemption_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    reference_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    subject = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    type = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_oauth_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_identity_oauth_tokens_identity_oauth_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "identity_oauth_applications",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_identity_oauth_tokens_identity_oauth_authorizations_authorization_id",
                        column: x => x.authorization_id,
                        principalTable: "identity_oauth_authorizations",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_identity_oauth_applications_client_id",
                table: "identity_oauth_applications",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_oauth_applications_tenant_id",
                table: "identity_oauth_applications",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_oauth_applications_tenant_id_client_id",
                table: "identity_oauth_applications",
                columns: new[] { "tenant_id", "client_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_identity_oauth_authorizations_application_id_status_subject_type",
                table: "identity_oauth_authorizations",
                columns: new[] { "application_id", "status", "subject", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_identity_oauth_authorizations_tenant_id",
                table: "identity_oauth_authorizations",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_oauth_scopes_name",
                table: "identity_oauth_scopes",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_identity_oauth_tokens_application_id_status_subject_type",
                table: "identity_oauth_tokens",
                columns: new[] { "application_id", "status", "subject", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_identity_oauth_tokens_authorization_id",
                table: "identity_oauth_tokens",
                column: "authorization_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_oauth_tokens_reference_id",
                table: "identity_oauth_tokens",
                column: "reference_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_identity_oauth_tokens_tenant_id",
                table: "identity_oauth_tokens",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_user_passkeys_user_id",
                table: "identity_user_passkeys",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "identity_data_protection_keys");

            migrationBuilder.DropTable(
                name: "identity_oauth_scopes");

            migrationBuilder.DropTable(
                name: "identity_oauth_tokens");

            migrationBuilder.DropTable(
                name: "identity_server_keys");

            migrationBuilder.DropTable(
                name: "identity_user_passkeys");

            migrationBuilder.DropTable(
                name: "identity_oauth_authorizations");

            migrationBuilder.DropTable(
                name: "identity_oauth_applications");

            migrationBuilder.DropColumn(
                name: "is_service_account",
                table: "identity_users");
        }
    }
}
