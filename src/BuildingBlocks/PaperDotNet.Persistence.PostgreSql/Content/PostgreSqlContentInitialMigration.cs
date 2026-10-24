using Microsoft.EntityFrameworkCore.Migrations;

namespace PaperDotNet.Persistence.PostgreSql.Content;

/// <summary>Exact initial migration for the pinned PaperGo development schema. Its own metadata records the version.</summary>
internal sealed class PostgreSqlContentInitialMigration(string schemaSql) : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(schemaSql);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Organization data is never dropped by an automatic down migration.");
}
