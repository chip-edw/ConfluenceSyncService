using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConfluenceSyncService.Migrations
{
    /// <inheritdoc />
    public partial class RecreatIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TaskIdMap_AckExpiresUtc",
                table: "TaskIdMap",
                column: "AckExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TaskIdMap_CorrelationId",
                table: "TaskIdMap",
                column: "CorrelationId");

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

            migrationBuilder.CreateIndex(
                name: "IX_TaskIdMap_CustomerId_PhaseName_TaskName_WorkflowId",
                table: "TaskIdMap",
                columns: new[] { "CustomerId", "PhaseName", "TaskName", "WorkflowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskIdMap_AckExpiresUtc",
                table: "TaskIdMap");

            migrationBuilder.DropIndex(
                name: "IX_TaskIdMap_CorrelationId",
                table: "TaskIdMap");

            migrationBuilder.DropIndex(
                name: "IX_TaskIdMap_NextChaseAtUtcCached",
                table: "TaskIdMap");

            migrationBuilder.DropIndex(
                name: "IX_TaskIdMap_SpItemId",
                table: "TaskIdMap");

            migrationBuilder.DropIndex(
                name: "IX_TaskIdMap_TeamId_ChannelId",
                table: "TaskIdMap");

            migrationBuilder.DropIndex(
                name: "IX_TaskIdMap_CustomerId_PhaseName_TaskName_WorkflowId",
                table: "TaskIdMap");
        }
    }
}
