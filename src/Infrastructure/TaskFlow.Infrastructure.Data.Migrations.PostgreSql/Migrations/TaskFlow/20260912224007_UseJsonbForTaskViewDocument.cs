using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskFlow.Infrastructure.Data.Migrations.PostgreSql.Migrations.TaskFlow
{
    /// <inheritdoc />
    public partial class UseJsonbForTaskViewDocument : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing values came from TaskViewBodyJsonContext. PostgreSQL requires an explicit cast from
            // text to jsonb; invalid legacy JSON must fail this migration instead of being discarded.
            migrationBuilder.Sql(
                """
                ALTER TABLE taskflow."TaskView"
                ALTER COLUMN "Document" TYPE jsonb
                USING "Document"::jsonb;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE taskflow."TaskView"
                ALTER COLUMN "Document" TYPE text
                USING "Document"::text;
                """);
        }
    }
}
