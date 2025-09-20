namespace ConfluenceSyncService.Options;

public sealed class ChaserJobOptions
{
    public bool DryRun { get; set; } = false; // Default to false for production
    public bool Enabled { get; set; } = false;
    public int CadenceMinutes { get; set; } = 15;
    public int BatchSize { get; set; } = 50;
    public string QuerySource { get; set; } = "SQLiteFirst"; // "SharePoint"|"SQLiteFirst"|"Hybrid"
    public int SendHourLocal { get; set; } = 9;
    public BusinessWindowOptions BusinessWindow { get; set; } = new();
    public string ThreadFallback { get; set; } = "RootNew"; // "RootNew"|"Skip"
    public SafetyOptions Safety { get; set; } = new();

    public sealed class BusinessWindowOptions
    {
        public int StartHourLocal { get; set; } = 8;
        public int EndHourLocal { get; set; } = 18;
        public int CushionHours { get; set; } = 12;
    }

    public sealed class SafetyOptions
    {
        public int MaxConsecutiveFailures { get; set; } = 5;
        public int CoolOffMinutes { get; set; } = 5;
    }
}
