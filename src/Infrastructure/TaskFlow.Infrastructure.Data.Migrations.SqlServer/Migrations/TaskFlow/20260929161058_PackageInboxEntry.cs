using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskFlow.Infrastructure.Data.Migrations.SqlServer.Migrations.TaskFlow
{
    /// <inheritdoc />
    public partial class PackageInboxEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQL Server cannot alter a column that a primary key depends on, so the key is dropped around it.
            migrationBuilder.DropPrimaryKey(
                name: "PK_ConsumerInbox",
                schema: "taskflow",
                table: "ConsumerInbox");

            migrationBuilder.AlterColumn<string>(
                name: "Consumer",
                schema: "taskflow",
                table: "ConsumerInbox",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConsumerInbox",
                schema: "taskflow",
                table: "ConsumerInbox",
                columns: new[] { "Consumer", "MessageId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQL Server cannot alter a column that a primary key depends on, so the key is dropped around it.
            migrationBuilder.DropPrimaryKey(
                name: "PK_ConsumerInbox",
                schema: "taskflow",
                table: "ConsumerInbox");

            migrationBuilder.AlterColumn<string>(
                name: "Consumer",
                schema: "taskflow",
                table: "ConsumerInbox",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConsumerInbox",
                schema: "taskflow",
                table: "ConsumerInbox",
                columns: new[] { "Consumer", "MessageId" });
        }
    }
}
