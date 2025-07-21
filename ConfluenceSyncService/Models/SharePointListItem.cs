namespace ConfluenceSyncService.Models
{
    public class SharePointListItem
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public DateTime LastModifiedUtc { get; set; } = DateTime.MinValue; // ADD THIS
        public Dictionary<string, object> Fields { get; set; } = new(); // ADD THIS
    }
}