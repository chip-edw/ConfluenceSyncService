namespace ConfluenceSyncService.Models
{
    public class ConfluenceTokenInfo
    {
        //These will not be persisted to SQLite. They are only for in Memory storage
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string CloudId { get; set; }

        public bool IsExpired() =>
            DateTimeOffset.UtcNow > ExpiresAt.AddMinutes(-5);
    }
}
