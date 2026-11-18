using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskFlow.Infrastructure.Data.Migrations.PostgreSql.Migrations.Scheduler
{
    /// <inheritdoc />
    public partial class InitialScheduler : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "scheduler");

            migrationBuilder.CreateTable(
                name: "CronJobState",
                schema: "scheduler",
                columns: table => new
                {
                    JobName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LastOccurrenceUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CronJobState", x => x.JobName);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledJob",
                schema: "scheduler",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: true),
                    CronOccurrenceUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LeaseExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    DeadLetteredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledJob", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledJobDefinition",
                schema: "scheduler",
                columns: table => new
                {
                    JobName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Cron = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Timeout = table.Column<long>(type: "bigint", nullable: false),
                    PublishedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledJobDefinition", x => x.JobName);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledJobRun",
                schema: "scheduler",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CronOccurrenceUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Node = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledJobRun", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledJob_Dispatch",
                schema: "scheduler",
                table: "ScheduledJob",
                columns: new[] { "DeadLetteredAtUtc", "AvailableAtUtc", "LeaseExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledJob_LeaseToken",
                schema: "scheduler",
                table: "ScheduledJob",
                column: "LeaseToken");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledJobRun_Finished",
                schema: "scheduler",
                table: "ScheduledJobRun",
                column: "FinishedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledJobRun_Job",
                schema: "scheduler",
                table: "ScheduledJobRun",
                columns: new[] { "JobName", "FinishedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CronJobState",
                schema: "scheduler");

            migrationBuilder.DropTable(
                name: "ScheduledJob",
                schema: "scheduler");

            migrationBuilder.DropTable(
                name: "ScheduledJobDefinition",
                schema: "scheduler");

            migrationBuilder.DropTable(
                name: "ScheduledJobRun",
                schema: "scheduler");
        }
    }
}
