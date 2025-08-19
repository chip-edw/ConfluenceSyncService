namespace ConfluenceSyncService.Services.Sync
{
    public sealed class TrackerDeltaItem
    {
        public string ItemId { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string CustomerName { get; init; } = "";
        public string Region { get; init; } = "";
        public string PhaseName { get; init; } = "";
        public DateTimeOffset? GoLive { get; init; }
        public DateTimeOffset? HypercareEnd { get; init; }
        public DateTimeOffset LastModifiedUtc { get; init; }
    }
}
