namespace ConfluenceSyncService.Dtos
{
    public class ConfluenceTokenResponse
    {
        public string access_token { get; set; } = default!;
        public string refresh_token { get; set; } = default!;
        public int expires_in { get; set; }
    }
}

