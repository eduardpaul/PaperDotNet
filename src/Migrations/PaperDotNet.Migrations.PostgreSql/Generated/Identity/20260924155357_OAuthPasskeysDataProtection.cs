using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Identity
{
    /// <inheritdoc />
    public partial class OAuthPasskeysDataProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "phone_number",
                schema: "identity",
                table: "users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_service_account",
                schema: "identity",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<string>(
                name: "name",
                schema: "identity",
                table: "user_tokens",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "login_provider",
                schema: "identity",
                table: "user_tokens",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "provider_key",
                schema: "identity",
                table: "user_logins",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "login_provider",
                schema: "identity",
                table: "user_logins",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "oauth_applications",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    application_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    client_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    client_secret = table.Column<string>(type: "text", nullable: true),
                    client_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    consent_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    display_names = table.Column<string>(type: "text", nullable: true),
                    json_web_key_set = table.Column<string>(type: "text", nullable: true),
                    permissions = table.Column<string>(type: "text", nullable: true),
                    post_logout_redirect_uris = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    redirect_uris = table.Column<string>(type: "text", nullable: true),
                    requirements = table.Column<string>(type: "text", nullable: true),
                    settings = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_applications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "oauth_scopes",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    descriptions = table.Column<string>(type: "text", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    display_names = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    resources = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_scopes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "server_keys",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    use = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    protected_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_passkeys",
                schema: "identity",
                columns: table => new
                {
                    credential_id = table.Column<byte[]>(type: "bytea", maxLength: 1024, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_passkeys", x => x.credential_id);
                    table.ForeignKey(
                        name: "fk_user_passkeys_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oauth_authorizations",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    creation_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    scopes = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_authorizations", x => x.id);
                    table.ForeignKey(
                        name: "fk_oauth_authorizations_oauth_applications_application_id",
                        column: x => x.application_id,
                        principalSchema: "identity",
                        principalTable: "oauth_applications",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "oauth_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    authorization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    creation_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expiration_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    payload = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    redemption_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reference_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    type = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_oauth_tokens_oauth_applications_application_id",
                        column: x => x.application_id,
                        principalSchema: "identity",
                        principalTable: "oauth_applications",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_oauth_tokens_oauth_authorizations_authorization_id",
                        column: x => x.authorization_id,
                        principalSchema: "identity",
                        principalTable: "oauth_authorizations",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_oauth_applications_client_id",
                schema: "identity",
                table: "oauth_applications",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_applications_tenant_id",
                schema: "identity",
                table: "oauth_applications",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_applications_tenant_id_client_id",
                schema: "identity",
                table: "oauth_applications",
                columns: new[] { "tenant_id", "client_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_authorizations_application_id_status_subject_type",
                schema: "identity",
                table: "oauth_authorizations",
                columns: new[] { "application_id", "status", "subject", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_oauth_authorizations_tenant_id",
                schema: "identity",
                table: "oauth_authorizations",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_scopes_name",
                schema: "identity",
                table: "oauth_scopes",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_tokens_application_id_status_subject_type",
                schema: "identity",
                table: "oauth_tokens",
                columns: new[] { "application_id", "status", "subject", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_oauth_tokens_authorization_id",
                schema: "identity",
                table: "oauth_tokens",
                column: "authorization_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_tokens_reference_id",
                schema: "identity",
                table: "oauth_tokens",
                column: "reference_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_tokens_tenant_id",
                schema: "identity",
                table: "oauth_tokens",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_passkeys_user_id",
                schema: "identity",
                table: "user_passkeys",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_protection_keys",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "oauth_scopes",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "oauth_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "server_keys",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_passkeys",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "oauth_authorizations",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "oauth_applications",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "is_service_account",
                schema: "identity",
                table: "users");

            migrationBuilder.AlterColumn<string>(
                name: "phone_number",
                schema: "identity",
                table: "users",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "name",
                schema: "identity",
                table: "user_tokens",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "login_provider",
                schema: "identity",
                table: "user_tokens",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "provider_key",
                schema: "identity",
                table: "user_logins",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "login_provider",
                schema: "identity",
                table: "user_logins",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);
        }
    }
}
