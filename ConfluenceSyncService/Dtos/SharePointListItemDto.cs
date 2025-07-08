namespace ConfluenceSyncService.Dtos
{
    public class SharePointListItemDto
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public DateTime LastModifiedUtc { get; set; }

        public Dictionary<string, object> Fields { get; set; } = new();
    }

}
