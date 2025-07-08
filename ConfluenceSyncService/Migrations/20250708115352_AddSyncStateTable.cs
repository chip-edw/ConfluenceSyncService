using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConfluenceSyncService.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncStateTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                    LastSource = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncStates_SharePointId_ConfluenceId",
                table: "SyncStates",
                columns: new[] { "SharePointId", "ConfluenceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncStates");
        }
    }
}
