namespace ConfluenceSyncService.Models
{
    public class ConfluencePage
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string WebUrl { get; set; } = string.Empty;
        public int Version { get; set; }

        // Page content - we'll populate this when we fetch full page details
        public string? HtmlContent { get; set; }
        public string? AdfContent { get; set; }

        // Extracted customer name from title (e.g., "Acme Corp" from "Acme Corp - Transition Tracker")
        public string CustomerName { get; set; } = string.Empty;

        // Database info found on this page (if any)
        public bool HasDatabase { get; set; }
        public string? DatabaseId { get; set; }
        public List<ConfluencePageDatabaseRow> DatabaseRows { get; set; } = new();
    }

    public class ConfluencePageDatabaseRow
    {
        public string Id { get; set; } = string.Empty;
        public Dictionary<string, object> Fields { get; set; } = new();
        public DateTime LastModified { get; set; }
    }
}
