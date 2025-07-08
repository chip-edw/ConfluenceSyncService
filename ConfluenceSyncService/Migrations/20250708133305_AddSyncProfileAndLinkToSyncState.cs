using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConfluenceSyncService.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncProfileAndLinkToSyncState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SyncProfileId",
                table: "SyncStates",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

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

            migrationBuilder.CreateIndex(
                name: "IX_SyncStates_SyncProfileId",
                table: "SyncStates",
                column: "SyncProfileId");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncStates_SyncProfile_SyncProfileId",
                table: "SyncStates",
                column: "SyncProfileId",
                principalTable: "SyncProfile",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncStates_SyncProfile_SyncProfileId",
                table: "SyncStates");

            migrationBuilder.DropTable(
                name: "SyncProfile");

            migrationBuilder.DropIndex(
                name: "IX_SyncStates_SyncProfileId",
                table: "SyncStates");

            migrationBuilder.DropColumn(
                name: "SyncProfileId",
                table: "SyncStates");
        }
    }
}
