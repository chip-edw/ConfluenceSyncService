namespace ConfluenceSyncService.Dtos
{
    public class ConfluenceDatabaseEntryDto
    {
        public string Id { get; set; }
        public DateTime LastModifiedUtc { get; set; }
        public Dictionary<string, object> Fields { get; set; } = new();
    }

}
