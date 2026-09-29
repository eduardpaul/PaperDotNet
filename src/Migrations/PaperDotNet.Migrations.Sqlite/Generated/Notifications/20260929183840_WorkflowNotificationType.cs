using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Notifications
{
    /// <inheritdoc />
    public partial class WorkflowNotificationType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-0036: the notification type automation was renamed to workflow; chosen channels and the inbox keep it.
            migrationBuilder.Sql("""UPDATE notifications_settings SET channels = replace(channels, '"automation":', '"workflow":') WHERE channels LIKE '%"automation":%' AND channels NOT LIKE '%"workflow":%';""");
            migrationBuilder.Sql("""UPDATE notifications_notifications SET type = 'workflow' WHERE type = 'automation';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE notifications_settings SET channels = replace(channels, '"workflow":', '"automation":') WHERE channels LIKE '%"workflow":%' AND channels NOT LIKE '%"automation":%';""");
            migrationBuilder.Sql("""UPDATE notifications_notifications SET type = 'automation' WHERE type = 'workflow';""");
        }
    }
}
