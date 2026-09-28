using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskFlow.Infrastructure.Data.Migrations.PostgreSql.Migrations.TaskFlow
{
    /// <inheritdoc />
    public partial class TwoStateInboxAndOutboxTraceContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConsumerInbox_ProcessedAtUtc",
                schema: "taskflow",
                table: "ConsumerInbox");

            migrationBuilder.RenameColumn(
                name: "ProcessedAtUtc",
                schema: "taskflow",
                table: "ConsumerInbox",
                newName: "ClaimedAtUtc");

            migrationBuilder.AddColumn<string>(
                name: "TraceParent",
                schema: "taskflow",
                table: "OutboxMessage",
                type: "character varying(55)",
                maxLength: 55,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceState",
                schema: "taskflow",
                table: "OutboxMessage",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimToken",
                schema: "taskflow",
                table: "ConsumerInbox",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAtUtc",
                schema: "taskflow",
                table: "ConsumerInbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresUtc",
                schema: "taskflow",
                table: "ConsumerInbox",
                type: "timestamp with time zone",
                nullable: true);

            // Under the one-state inbox every existing claim already counted as processed - a redelivery that
            // found it was skipped - so backfilling each as completed keeps that meaning. Each row also gets its
            // own ClaimToken: the retention purge batches on it and needs it unique.
            migrationBuilder.Sql(
                @"UPDATE taskflow.""ConsumerInbox"" SET ""ClaimToken"" = gen_random_uuid(), ""CompletedAtUtc"" = ""ClaimedAtUtc"";");

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerInbox_Retention",
                schema: "taskflow",
                table: "ConsumerInbox",
                columns: new[] { "CompletedAtUtc", "LeaseExpiresUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConsumerInbox_Retention",
                schema: "taskflow",
                table: "ConsumerInbox");

            migrationBuilder.DropColumn(
                name: "TraceParent",
                schema: "taskflow",
                table: "OutboxMessage");

            migrationBuilder.DropColumn(
                name: "TraceState",
                schema: "taskflow",
                table: "OutboxMessage");

            migrationBuilder.DropColumn(
                name: "ClaimToken",
                schema: "taskflow",
                table: "ConsumerInbox");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                schema: "taskflow",
                table: "ConsumerInbox");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresUtc",
                schema: "taskflow",
                table: "ConsumerInbox");

            migrationBuilder.RenameColumn(
                name: "ClaimedAtUtc",
                schema: "taskflow",
                table: "ConsumerInbox",
                newName: "ProcessedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerInbox_ProcessedAtUtc",
                schema: "taskflow",
                table: "ConsumerInbox",
                column: "ProcessedAtUtc");
        }
    }
}
