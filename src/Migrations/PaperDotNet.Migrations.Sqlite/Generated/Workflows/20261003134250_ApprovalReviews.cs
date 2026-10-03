using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workflows
{
    /// <inheritdoc />
    public partial class ApprovalReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "review_key",
                table: "automation_approvals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_type",
                table: "automation_approvals",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "review_key",
                table: "automation_approvals");

            migrationBuilder.DropColumn(
                name: "review_type",
                table: "automation_approvals");
        }
    }
}
