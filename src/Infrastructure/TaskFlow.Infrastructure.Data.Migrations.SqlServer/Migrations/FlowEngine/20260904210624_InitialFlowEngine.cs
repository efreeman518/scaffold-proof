using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskFlow.Infrastructure.Data.Migrations.SqlServer.Migrations.FlowEngine
{
    /// <inheritdoc />
    public partial class InitialFlowEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "flowengine");

            migrationBuilder.CreateTable(
                name: "ChildSignals",
                schema: "flowengine",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ParentInstanceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChildSignals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CircuitBreakers",
                schema: "flowengine",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OpenUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailureCount = table.Column<int>(type: "int", nullable: false),
                    FailureTimestampsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CircuitBreakers", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Executions",
                schema: "flowengine",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    WorkflowId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ParentInstanceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    EventName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CorrelationKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TimeoutAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Deadline = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Data = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Executions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HumanTasks",
                schema: "flowengine",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    InstanceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AssignedTo = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Data = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HumanTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                schema: "flowengine",
                columns: table => new
                {
                    EntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NodeId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    ClientRef = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    EnqueuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextRetryAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.EntryId);
                });

            migrationBuilder.CreateTable(
                name: "Workflows",
                schema: "flowengine",
                columns: table => new
                {
                    CompositeKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    WorkflowId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Data = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workflows", x => x.CompositeKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChildSignals_ParentInstanceId",
                schema: "flowengine",
                table: "ChildSignals",
                column: "ParentInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_CircuitBreakers_OpenUntil",
                schema: "flowengine",
                table: "CircuitBreakers",
                column: "OpenUntil");

            migrationBuilder.CreateIndex(
                name: "IX_Executions_EventName_CorrelationKey",
                schema: "flowengine",
                table: "Executions",
                columns: new[] { "EventName", "CorrelationKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Executions_ParentInstanceId",
                schema: "flowengine",
                table: "Executions",
                column: "ParentInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_Executions_Status",
                schema: "flowengine",
                table: "Executions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Executions_WorkflowId",
                schema: "flowengine",
                table: "Executions",
                column: "WorkflowId");

            migrationBuilder.CreateIndex(
                name: "IX_HumanTasks_AssignedTo_Status",
                schema: "flowengine",
                table: "HumanTasks",
                columns: new[] { "AssignedTo", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_HumanTasks_InstanceId",
                schema: "flowengine",
                table: "HumanTasks",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_InstanceId_NodeId",
                schema: "flowengine",
                table: "Outbox",
                columns: new[] { "InstanceId", "NodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_NextRetryAt",
                schema: "flowengine",
                table: "Outbox",
                column: "NextRetryAt");

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_PublishedAt",
                schema: "flowengine",
                table: "Outbox",
                column: "PublishedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChildSignals",
                schema: "flowengine");

            migrationBuilder.DropTable(
                name: "CircuitBreakers",
                schema: "flowengine");

            migrationBuilder.DropTable(
                name: "Executions",
                schema: "flowengine");

            migrationBuilder.DropTable(
                name: "HumanTasks",
                schema: "flowengine");

            migrationBuilder.DropTable(
                name: "Outbox",
                schema: "flowengine");

            migrationBuilder.DropTable(
                name: "Workflows",
                schema: "flowengine");
        }
    }
}
