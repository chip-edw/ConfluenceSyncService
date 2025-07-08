namespace ConfluenceSyncService.Models
{
    public class ConfluenceRow
    {
        public string ExternalId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public Dictionary<string, object> Fields { get; set; } = new();
    }
}
