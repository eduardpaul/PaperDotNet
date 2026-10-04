using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workflows
{
    /// <inheritdoc />
    public partial class ParameterizedItemTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE automation_definitions SET "enabled" = 0
                WHERE "trigger" = 'tagAdded' OR "trigger" LIKE 'tagAdded,%'
                   OR "trigger" LIKE '%,tagAdded,%' OR "trigger" LIKE '%,tagAdded';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Do not re-enable workflows: their prior enabled state cannot be inferred safely.
        }
    }
}
