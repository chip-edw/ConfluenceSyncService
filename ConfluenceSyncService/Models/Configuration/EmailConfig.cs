namespace ConfluenceSyncService.Models.Configuration
{
    public class EmailConfig
    {
        public string? FromEmail { get; set; }
        public string? FromDisplayName { get; set; }
        public Dictionary<string, EmailNotificationConfig>? Notifications { get; set; }
    }

    public class EmailNotificationConfig
    {
        public string? FromEmail { get; set; }
        public string? FromDisplayName { get; set; }
        public string? ToEmail { get; set; }
    }
}