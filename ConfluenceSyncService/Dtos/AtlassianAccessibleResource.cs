namespace ConfluenceSyncService.Dtos
{
    public class AtlassianAccessibleResource
    {
        public string id { get; set; } = default!;
        public string url { get; set; } = default!;
        public string name { get; set; } = default!;
        public string[] scopes { get; set; } = Array.Empty<string>();
        public string avatarUrl { get; set; } = default!;
        public string product { get; set; } = default!;
    }
}
