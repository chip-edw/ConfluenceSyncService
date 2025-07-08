namespace ConfluenceSyncService.Models
{
    public class SharePointListItem
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; }
        public string Status { get; set; }
        public string Owner { get; set; }

        // Add additional fields once your schema is known
    }
}
