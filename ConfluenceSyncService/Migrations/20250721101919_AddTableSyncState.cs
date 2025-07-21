using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConfluenceSyncService.Migrations
{
    /// <inheritdoc />
    public partial class AddTableSyncState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TableSyncStates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ConfluencePageId = table.Column<string>(type: "TEXT", nullable: false),
                    SharePointItemId = table.Column<string>(type: "TEXT", nullable: false),
                    CustomerName = table.Column<string>(type: "TEXT", nullable: false),
                    LastConfluenceModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSharePointModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncSource = table.Column<string>(type: "TEXT", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "TEXT", nullable: true),
                    LastErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ConfluencePageVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TableSyncStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TableSyncStates_ConfluencePageId",
                table: "TableSyncStates",
                column: "ConfluencePageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TableSyncStates");
        }
    }
}
