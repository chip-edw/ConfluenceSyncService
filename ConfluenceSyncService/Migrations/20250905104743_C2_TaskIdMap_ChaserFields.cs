using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConfluenceSyncService.Migrations
{
    /// <inheritdoc />
    public partial class C2_TaskIdMap_ChaserFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnchorDateType",
                table: "TaskIdMap",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Region",
                table: "TaskIdMap",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            // Idempotent indexes (won’t fail if they already exist)
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS IX_TaskIdMap_NextChaseAtUtcCached ON TaskIdMap(NextChaseAtUtcCached);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS IX_TaskIdMap_AckExpiresUtc ON TaskIdMap(AckExpiresUtc);");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop only what this migration added.
            // Indexes were created via raw SQL in Up(), so make drops idempotent too.
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_TaskIdMap_NextChaseAtUtcCached;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_TaskIdMap_AckExpiresUtc;");

            migrationBuilder.DropColumn(
                name: "Region",
                table: "TaskIdMap");

            migrationBuilder.DropColumn(
                name: "AnchorDateType",
                table: "TaskIdMap");
        }

    }
}
