using System.ComponentModel.DataAnnotations;

namespace ConfluenceSyncService.Models
{
    public class TableSyncState
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string ConfluencePageId { get; set; } = string.Empty;
        public string SharePointItemId { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public string? CustomerId { get; set; }
        public DateTime? LastConfluenceModifiedUtc { get; set; }
        public DateTime? LastSharePointModifiedUtc { get; set; }
        public DateTime? LastSyncedUtc { get; set; }

        public string? LastSyncSource { get; set; } // "Confluence" or "SharePoint"
        public string? LastSyncStatus { get; set; } // "Success", "Failed", "Conflict"
        public string? LastErrorMessage { get; set; }

        public int ConfluencePageVersion { get; set; } = 0;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string? SyncTracker { get; set; }
    }
}
