using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConfluenceSyncService.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfigStore",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ValueName = table.Column<string>(type: "TEXT", nullable: false),
                    ValueType = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigStore", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncProfile",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileName = table.Column<string>(type: "TEXT", nullable: false),
                    SharePointSiteId = table.Column<string>(type: "TEXT", nullable: false),
                    SharePointListId = table.Column<string>(type: "TEXT", nullable: false),
                    ConfluenceSpaceKey = table.Column<string>(type: "TEXT", nullable: false),
                    ConfluenceDatabaseId = table.Column<string>(type: "TEXT", nullable: false),
                    ConfluenceDashboardPageId = table.Column<string>(type: "TEXT", nullable: true),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncProfile", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TableSyncStates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ConfluencePageId = table.Column<string>(type: "TEXT", nullable: false),
                    SharePointItemId = table.Column<string>(type: "TEXT", nullable: false),
                    CustomerName = table.Column<string>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<string>(type: "TEXT", nullable: true),
                    LastConfluenceModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSharePointModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncSource = table.Column<string>(type: "TEXT", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "TEXT", nullable: true),
                    LastErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ConfluencePageVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SyncTracker = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TableSyncStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskIdMap",
                columns: table => new
                {
                    TaskId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ListKey = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "PhaseTasks"),
                    SpItemId = table.Column<string>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: true),
                    CustomerId = table.Column<string>(type: "TEXT", nullable: true),
                    PhaseName = table.Column<string>(type: "TEXT", nullable: true),
                    TaskName = table.Column<string>(type: "TEXT", nullable: true),
                    WorkflowId = table.Column<string>(type: "TEXT", nullable: true),
                    Category_Key = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "reserved"),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TeamId = table.Column<string>(type: "TEXT", nullable: true),
                    ChannelId = table.Column<string>(type: "TEXT", nullable: true),
                    RootMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    LastMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    AckVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    AckExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextChaseAtUtcCached = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastChaseAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Region = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AnchorDateType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    StartOffsetDays = table.Column<int>(type: "INTEGER", nullable: true),
                    DueDateUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskIdMap", x => x.TaskId);
                });

            migrationBuilder.CreateTable(
                name: "SyncStates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SharePointId = table.Column<string>(type: "TEXT", nullable: true),
                    ConfluenceId = table.Column<string>(type: "TEXT", nullable: true),
                    LastSharePointModifiedUtc = table.Column<string>(type: "TEXT", nullable: true),
                    LastConfluenceModifiedUtc = table.Column<string>(type: "TEXT", nullable: true),
                    LastSyncedUtc = table.Column<string>(type: "TEXT", nullable: true),
                    LastSource = table.Column<string>(type: "TEXT", nullable: true),
                    SyncProfileId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncStates_SyncProfile_SyncProfileId",
                        column: x => x.SyncProfileId,
                        principalTable: "SyncProfile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncStates_SharePointId_ConfluenceId",
                table: "SyncStates",
                columns: new[] { "SharePointId", "ConfluenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncStates_SyncProfileId",
                table: "SyncStates",
                column: "SyncProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_TableSyncStates_ConfluencePageId",
                table: "TableSyncStates",
                column: "ConfluencePageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskIdMap_AckExpiresUtc",
                table: "TaskIdMap",
                column: "AckExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TaskIdMap_CorrelationId",
                table: "TaskIdMap",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskIdMap_CustomerId_PhaseName_TaskName_WorkflowId",
                table: "TaskIdMap",
                columns: new[] { "CustomerId", "PhaseName", "TaskName", "WorkflowId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskIdMap_NextChaseAtUtcCached",
                table: "TaskIdMap",
                column: "NextChaseAtUtcCached");

            migrationBuilder.CreateIndex(
                name: "IX_TaskIdMap_SpItemId",
                table: "TaskIdMap",
                column: "SpItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskIdMap_TeamId_ChannelId",
                table: "TaskIdMap",
                columns: new[] { "TeamId", "ChannelId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfigStore");

            migrationBuilder.DropTable(
                name: "SyncStates");

            migrationBuilder.DropTable(
                name: "TableSyncStates");

            migrationBuilder.DropTable(
                name: "TaskIdMap");

            migrationBuilder.DropTable(
                name: "SyncProfile");
        }
    }
}
