using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConfluenceSyncService.Migrations
{
    /// <inheritdoc />
    public partial class FixTimestampData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fix existing records with actual timestamps
            migrationBuilder.Sql("UPDATE ConfigStore SET CreatedAt = datetime('now'), UpdatedAt = datetime('now') WHERE CreatedAt = 'CreatedAt' OR UpdatedAt = 'UpdatedAt';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No down migration needed for data fix
        }
    }
}