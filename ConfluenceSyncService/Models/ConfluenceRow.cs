
namespace ConfluenceSyncService.Models
{
    public class ConfluenceRow
    {
        public string Id { get; set; } = string.Empty; // Confluence row ID
        public string ExternalId { get; set; } = string.Empty; // Matches SharePoint item ID
        public string Title { get; set; } = string.Empty;

        public DateTime LastModifiedUtc { get; set; } = DateTime.MinValue;

        public Dictionary<string, object> Fields { get; set; } = new();
    }
}
