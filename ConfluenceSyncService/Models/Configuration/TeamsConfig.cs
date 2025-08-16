// Add this to your Models/Configuration folder

namespace ConfluenceSyncService.Models.Configuration
{
    public class TeamsConfig
    {
        public string? Team { get; set; }
        public string TeamId { get; set; } = "";
        public string? Channel { get; set; }
        public string ChannelId { get; set; } = "";
    }
}