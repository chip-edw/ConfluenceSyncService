using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConfluenceSyncService.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerIdToTableSyncState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StartOffsetDays",
                table: "TaskIdMap",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerId",
                table: "TableSyncStates",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StartOffsetDays",
                table: "TaskIdMap");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "TableSyncStates");
        }
    }
}
