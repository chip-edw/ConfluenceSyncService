using System.ComponentModel.DataAnnotations;

namespace ConfluenceSyncService.Models
{
    public class SyncProfile
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string ProfileName { get; set; }          // e.g., "SupportTransitionStatus"
        public string SharePointSiteId { get; set; }
        public string SharePointListId { get; set; }

        public string ConfluenceSpaceKey { get; set; }
        public string ConfluenceDatabaseId { get; set; } // optional: for dashboard sync, this could be null
        public string? ConfluenceDashboardPageId { get; set; } // if syncing a status block

        public string Direction { get; set; } = "BiDirectional"; // or "ToConfluence", "ToSharePoint"
        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

}
