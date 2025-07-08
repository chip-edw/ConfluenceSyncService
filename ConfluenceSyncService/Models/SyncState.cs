using System.ComponentModel.DataAnnotations;

namespace ConfluenceSyncService.Models
{
    public class SyncState
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string SharePointId { get; set; }
        public string ConfluenceId { get; set; }

        public string? LastSharePointModifiedUtc { get; set; }
        public string? LastConfluenceModifiedUtc { get; set; }

        public string? LastSyncedUtc { get; set; }
        public string? LastSource { get; set; } // "SharePoint" or "Confluence"

        public string SyncProfileId { get; set; }
        public SyncProfile SyncProfile { get; set; }

    }

}
