using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Workflows
{
    /// <inheritdoc />
    public partial class ApprovalReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "review_key",
                schema: "automation",
                table: "approvals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_type",
                schema: "automation",
                table: "approvals",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "review_key",
                schema: "automation",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "review_type",
                schema: "automation",
                table: "approvals");
        }
    }
}
