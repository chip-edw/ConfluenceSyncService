namespace ConfluenceSyncService.Models
{
    public class ConfluenceColorMappings
    {
        public Dictionary<string, string> StatusFF { get; set; } = new();
        public Dictionary<string, string> StatusCust { get; set; } = new();
        public Dictionary<string, string> SupportImpact { get; set; } = new();
        public Dictionary<string, string> SupportAccepted { get; set; } = new();
        public Dictionary<string, string> SyncTracker { get; set; } = new();
        public Dictionary<string, string> Region { get; set; } = new();
    }
}
