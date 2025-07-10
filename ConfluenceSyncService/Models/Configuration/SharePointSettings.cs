namespace ConfluenceSyncService.Models.Configuration
{
    public class SharePointListConfig
    {
        public string DisplayName { get; set; } = "";
        public string ConfluenceDatabaseId { get; set; } = "";
    }

    public class SharePointSiteConfig
    {
        public string DisplayName { get; set; } = "";
        public string SitePath { get; set; } = "";
        public string SiteId { get; set; } = "";
        public List<SharePointListConfig> Lists { get; set; } = new();
    }

    public class SharePointSettings
    {
        public string Hostname { get; set; } = "";
        public List<SharePointSiteConfig> Sites { get; set; } = new();
    }
}
