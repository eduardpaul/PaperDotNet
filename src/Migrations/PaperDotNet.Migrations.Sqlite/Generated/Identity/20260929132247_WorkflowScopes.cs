using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Identity
{
    /// <inheritdoc />
    public partial class WorkflowScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-0036: the automation scopes were renamed; granted scopes keep working.
            migrationBuilder.Sql("""UPDATE identity_roles SET scopes = replace(scopes, '"automation.read"', '"workflow.read"') WHERE scopes IS NOT NULL;""");
            migrationBuilder.Sql("""UPDATE identity_api_tokens SET scopes = replace(scopes, '"automation.read"', '"workflow.read"') WHERE scopes IS NOT NULL;""");
            migrationBuilder.Sql("""UPDATE identity_oauth_authorizations SET scopes = replace(scopes, '"automation.read"', '"workflow.read"') WHERE scopes IS NOT NULL;""");
            migrationBuilder.Sql("""UPDATE identity_oauth_applications SET permissions = replace(permissions, '"scp:automation.read"', '"scp:workflow.read"') WHERE permissions IS NOT NULL;""");
            migrationBuilder.Sql("""UPDATE identity_roles SET scopes = replace(scopes, '"automation.write"', '"workflow.write"') WHERE scopes IS NOT NULL;""");
            migrationBuilder.Sql("""UPDATE identity_api_tokens SET scopes = replace(scopes, '"automation.write"', '"workflow.write"') WHERE scopes IS NOT NULL;""");
            migrationBuilder.Sql("""UPDATE identity_oauth_authorizations SET scopes = replace(scopes, '"automation.write"', '"workflow.write"') WHERE scopes IS NOT NULL;""");
            migrationBuilder.Sql("""UPDATE identity_oauth_applications SET permissions = replace(permissions, '"scp:automation.write"', '"scp:workflow.write"') WHERE permissions IS NOT NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE identity_roles SET scopes = replace(scopes, '"workflow.read"', '"automation.read"') WHERE scopes IS NOT NULL;""");
            migrationBuilder.Sql("""UPDATE identity_api_tokens SET scopes = replace(scopes, '"workflow.read"', '"automation.read"') WHERE scopes IS NOT NULL;""");
            migrationBuilder.Sql("""UPDATE identity_oauth_authorizations SET scopes = replace(scopes, '"workflow.read"', '"automation.read"') WHERE scopes IS NOT NULL;""");
            migrationBuilder.Sql("""UPDATE identity_oauth_applications SET permissions = replace(permissions, '"scp:workflow.read"', '"scp:automation.read"') WHERE permissions IS NOT NULL;""");
            migrationBuilder.Sql("""UPDATE identity_roles SET scopes = replace(scopes, '"workflow.write"', '"automation.write"') WHERE scopes IS NOT NULL;""");
            migrationBuilder.Sql("""UPDATE identity_api_tokens SET scopes = replace(scopes, '"workflow.write"', '"automation.write"') WHERE scopes IS NOT NULL;""");
            migrationBuilder.Sql("""UPDATE identity_oauth_authorizations SET scopes = replace(scopes, '"workflow.write"', '"automation.write"') WHERE scopes IS NOT NULL;""");
            migrationBuilder.Sql("""UPDATE identity_oauth_applications SET permissions = replace(permissions, '"scp:workflow.write"', '"scp:automation.write"') WHERE permissions IS NOT NULL;""");
        }
    }
}
