using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConfluenceSyncService.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskIdMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                    State = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "reserved"),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TeamId = table.Column<string>(type: "TEXT", nullable: true),
                    ChannelId = table.Column<string>(type: "TEXT", nullable: true),
                    RootMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    LastMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    AckVersion = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    AckExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskIdMap", x => x.TaskId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskIdMap_CorrelationId",
                table: "TaskIdMap",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskIdMap_CustomerId_PhaseName_TaskName_WorkflowId",
                table: "TaskIdMap",
                columns: new[] { "CustomerId", "PhaseName", "TaskName", "WorkflowId" });

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
                name: "TaskIdMap");
        }
    }
}
