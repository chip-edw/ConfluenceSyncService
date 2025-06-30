namespace ConfluenceSyncService.Models
{
    public class SharePointConfig
    {
        public List<SharePointSiteConfig> Sites { get; set; } = new();
    }

    public class SharePointSiteConfig
    {
        public string DisplayName { get; set; } = string.Empty;
        public string SitePath { get; set; } = string.Empty;
        public string ConfluenceSpaceKey { get; set; } = string.Empty;
        public List<SharePointListConfig> Lists { get; set; } = new();
    }

    public class SharePointListConfig
    {
        public string DisplayName { get; set; } = string.Empty;
        public string ConfluenceDatabaseId { get; set; } = string.Empty;
    }
}