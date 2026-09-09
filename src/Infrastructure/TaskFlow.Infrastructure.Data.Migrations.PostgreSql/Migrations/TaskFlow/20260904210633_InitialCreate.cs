using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskFlow.Infrastructure.Data.Migrations.PostgreSql.Migrations.TaskFlow
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "taskflow");

            migrationBuilder.CreateTable(
                name: "Attachment",
                schema: "taskflow",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageUri = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    OwnerType = table.Column<int>(type: "integer", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachment", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "BlobDeleteWork",
                schema: "taskflow",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContainerName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    BlobName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_BlobDeleteWork", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Category",
                schema: "taskflow",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ParentCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Category", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Category_Category_TenantId_ParentCategoryId",
                        columns: x => new { x.TenantId, x.ParentCategoryId },
                        principalSchema: "taskflow",
                        principalTable: "Category",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConsumerInbox",
                schema: "taskflow",
                columns: table => new
                {
                    Consumer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumerInbox", x => new { x.Consumer, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "taskflow",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Destination = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventVersion = table.Column<int>(type: "integer", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_OutboxMessage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tag",
                schema: "taskflow",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tag", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "TaskItem",
                schema: "taskflow",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Features = table.Column<int>(type: "integer", nullable: false),
                    EstimatedEffort = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    ActualEffort = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    CompletedDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SecureDeterministic = table.Column<byte[]>(type: "bytea", maxLength: 256, nullable: true),
                    SecureRandom = table.Column<byte[]>(type: "bytea", maxLength: 256, nullable: true),
                    StartDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DueDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TerminalAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextOccurrenceAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OverdueNotifiedForDueDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RecurrenceTemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurrenceUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentTaskItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    SecureDeterministicBlindIndex = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecurrencePattern = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskItem", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_TaskItem_Category_TenantId_CategoryId",
                        columns: x => new { x.TenantId, x.CategoryId },
                        principalSchema: "taskflow",
                        principalTable: "Category",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskItem_TaskItem_TenantId_ParentTaskItemId",
                        columns: x => new { x.TenantId, x.ParentTaskItemId },
                        principalSchema: "taskflow",
                        principalTable: "TaskItem",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChecklistItem",
                schema: "taskflow",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CompletedDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TaskItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChecklistItem", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ChecklistItem_TaskItem_TenantId_TaskItemId",
                        columns: x => new { x.TenantId, x.TaskItemId },
                        principalSchema: "taskflow",
                        principalTable: "TaskItem",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Comment",
                schema: "taskflow",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    TaskItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comment", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Comment_TaskItem_TenantId_TaskItemId",
                        columns: x => new { x.TenantId, x.TaskItemId },
                        principalSchema: "taskflow",
                        principalTable: "TaskItem",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskItemTag",
                schema: "taskflow",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskItemTag", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_TaskItemTag_Tag_TenantId_TagId",
                        columns: x => new { x.TenantId, x.TagId },
                        principalSchema: "taskflow",
                        principalTable: "Tag",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskItemTag_TaskItem_TenantId_TaskItemId",
                        columns: x => new { x.TenantId, x.TaskItemId },
                        principalSchema: "taskflow",
                        principalTable: "TaskItem",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachment_TenantId_OwnerType_OwnerId_Id",
                schema: "taskflow",
                table: "Attachment",
                columns: new[] { "TenantId", "OwnerType", "OwnerId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_BlobDeleteWork_Dispatch",
                schema: "taskflow",
                table: "BlobDeleteWork",
                columns: new[] { "DeadLetteredAtUtc", "AvailableAtUtc", "LeaseExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BlobDeleteWork_LeaseToken",
                schema: "taskflow",
                table: "BlobDeleteWork",
                column: "LeaseToken");

            migrationBuilder.CreateIndex(
                name: "IX_Category_TenantId_Name",
                schema: "taskflow",
                table: "Category",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Category_TenantId_ParentCategoryId",
                schema: "taskflow",
                table: "Category",
                columns: new[] { "TenantId", "ParentCategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistItem_TenantId_TaskItemId_Id",
                schema: "taskflow",
                table: "ChecklistItem",
                columns: new[] { "TenantId", "TaskItemId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Comment_TenantId_TaskItemId_Id",
                schema: "taskflow",
                table: "Comment",
                columns: new[] { "TenantId", "TaskItemId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerInbox_ProcessedAtUtc",
                schema: "taskflow",
                table: "ConsumerInbox",
                column: "ProcessedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_Dispatch",
                schema: "taskflow",
                table: "OutboxMessage",
                columns: new[] { "DeadLetteredAtUtc", "AvailableAtUtc", "LeaseExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_LeaseToken",
                schema: "taskflow",
                table: "OutboxMessage",
                column: "LeaseToken");

            migrationBuilder.CreateIndex(
                name: "IX_Tag_TenantId_Name",
                schema: "taskflow",
                table: "Tag",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskItem_TenantId_CategoryId_Id",
                schema: "taskflow",
                table: "TaskItem",
                columns: new[] { "TenantId", "CategoryId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskItem_TenantId_DueDate_Id",
                schema: "taskflow",
                table: "TaskItem",
                columns: new[] { "TenantId", "DueDate", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskItem_TenantId_ModifiedAtUtc_Id",
                schema: "taskflow",
                table: "TaskItem",
                columns: new[] { "TenantId", "ModifiedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskItem_TenantId_NextOccurrenceAtUtc",
                schema: "taskflow",
                table: "TaskItem",
                columns: new[] { "TenantId", "NextOccurrenceAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskItem_TenantId_ParentTaskItemId",
                schema: "taskflow",
                table: "TaskItem",
                columns: new[] { "TenantId", "ParentTaskItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskItem_TenantId_Priority_Id",
                schema: "taskflow",
                table: "TaskItem",
                columns: new[] { "TenantId", "Priority", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskItem_TenantId_RecurrenceTemplateId_OccurrenceUtc",
                schema: "taskflow",
                table: "TaskItem",
                columns: new[] { "TenantId", "RecurrenceTemplateId", "OccurrenceUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskItem_TenantId_SecureDeterministicBlindIndex",
                schema: "taskflow",
                table: "TaskItem",
                columns: new[] { "TenantId", "SecureDeterministicBlindIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskItem_TenantId_Status_Id",
                schema: "taskflow",
                table: "TaskItem",
                columns: new[] { "TenantId", "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskItem_TenantId_TerminalAtUtc_Status",
                schema: "taskflow",
                table: "TaskItem",
                columns: new[] { "TenantId", "TerminalAtUtc", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskItem_TenantId_Title_Id",
                schema: "taskflow",
                table: "TaskItem",
                columns: new[] { "TenantId", "Title", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskItemTag_TenantId_TagId",
                schema: "taskflow",
                table: "TaskItemTag",
                columns: new[] { "TenantId", "TagId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskItemTag_TenantId_TaskItemId_TagId",
                schema: "taskflow",
                table: "TaskItemTag",
                columns: new[] { "TenantId", "TaskItemId", "TagId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Attachment",
                schema: "taskflow");

            migrationBuilder.DropTable(
                name: "BlobDeleteWork",
                schema: "taskflow");

            migrationBuilder.DropTable(
                name: "ChecklistItem",
                schema: "taskflow");

            migrationBuilder.DropTable(
                name: "Comment",
                schema: "taskflow");

            migrationBuilder.DropTable(
                name: "ConsumerInbox",
                schema: "taskflow");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "taskflow");

            migrationBuilder.DropTable(
                name: "TaskItemTag",
                schema: "taskflow");

            migrationBuilder.DropTable(
                name: "Tag",
                schema: "taskflow");

            migrationBuilder.DropTable(
                name: "TaskItem",
                schema: "taskflow");

            migrationBuilder.DropTable(
                name: "Category",
                schema: "taskflow");
        }
    }
}
