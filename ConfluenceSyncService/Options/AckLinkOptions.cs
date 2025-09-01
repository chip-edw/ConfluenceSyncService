// Options/AckLinkOptions.cs
namespace ConfluenceSyncService.Options
{
    public sealed class AckLinkOptions
    {
        public string? SigningKey { get; set; }
        public string? BaseUrl { get; set; }
        public int GraceDays { get; set; } = 1;
        public AckLinkPolicy Policy { get; set; } = new();
    }

    public sealed class AckLinkPolicy
    {
        public int InitialTtlCapHours { get; set; } = 336;
        public int CushionHours { get; set; } = 12;
        public int ChaserTtlHours { get; set; } = 36;
        public bool RequireLatestLink { get; set; } = true;
        public int AllowedPreviousLinks { get; set; } = 0;
    }

    public sealed class TeamsOptions
    {
        public string? Team { get; set; }
        public string? TeamId { get; set; }
        public string? Channel { get; set; }
        public string? ChannelId { get; set; }
        public bool Enabled { get; set; } = false; // flip true when ready
    }
}
