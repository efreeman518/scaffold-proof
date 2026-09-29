using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskFlow.Infrastructure.Data.Migrations.SqlServer.Migrations.TaskFlow
{
    /// <inheritdoc />
    public partial class PackageOutboxMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Headers",
                schema: "taskflow",
                table: "OutboxMessage",
                type: "nvarchar(max)",
                nullable: true);

            // M10: the tenant now rides as the TenantId outbox header (a JSON object of strings) that the transport
            // copies onto the broker message. Carry every pending and dead-lettered row's tenant over before the
            // column goes, so a row staged before the upgrade is still published with its TenantId property.
            migrationBuilder.Sql(
                @"UPDATE [taskflow].[OutboxMessage] SET [Headers] = CONCAT(N'{""TenantId"":""', LOWER(CONVERT(nvarchar(36), [TenantId])), N'""}');");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "taskflow",
                table: "OutboxMessage");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "taskflow",
                table: "OutboxMessage",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.Sql(
                @"UPDATE [taskflow].[OutboxMessage] SET [TenantId] = TRY_CONVERT(uniqueidentifier, JSON_VALUE([Headers], '$.TenantId')) WHERE TRY_CONVERT(uniqueidentifier, JSON_VALUE([Headers], '$.TenantId')) IS NOT NULL;");

            migrationBuilder.DropColumn(
                name: "Headers",
                schema: "taskflow",
                table: "OutboxMessage");
        }
    }
}
