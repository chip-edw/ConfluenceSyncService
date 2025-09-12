namespace ConfluenceSyncService.Options
{
    public class DatabaseMaintenanceOptions
    {
        public const string SectionName = "DatabaseMaintenance";

        public bool CheckpointEnabled { get; set; } = true;
        public double CheckpointIntervalHours { get; set; } = 168; // 1 week
        public string CheckpointMode { get; set; } = "FULL";
    }
}
