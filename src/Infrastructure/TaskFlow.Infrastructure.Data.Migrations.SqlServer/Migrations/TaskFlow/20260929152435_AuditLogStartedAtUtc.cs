using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskFlow.Infrastructure.Data.Migrations.SqlServer.Migrations.TaskFlow
{
    /// <inheritdoc />
    public partial class AuditLogStartedAtUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAtUtc",
                schema: "taskflow",
                table: "AuditLog",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Pre-2.0 rows carried no start instant; RecordedUtc (the entry id's UUIDv7 time) is the closest.
            migrationBuilder.Sql("UPDATE [taskflow].[AuditLog] SET [StartedAtUtc] = [RecordedUtc];");

            migrationBuilder.DropColumn(
                name: "StartTimeTicks",
                schema: "taskflow",
                table: "AuditLog");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StartedAtUtc",
                schema: "taskflow",
                table: "AuditLog");

            migrationBuilder.AddColumn<long>(
                name: "StartTimeTicks",
                schema: "taskflow",
                table: "AuditLog",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }
    }
}
