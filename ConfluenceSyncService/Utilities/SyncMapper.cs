using ConfluenceSyncService.Dtos;
using ConfluenceSyncService.Models.ConfluenceSyncService.Models;

namespace ConfluenceSyncService.Utilities
{
    public static class SyncMapper
    {
        public static ConfluenceRow MapToConfluenceRow(SharePointListItemDto item)
        {
            return new ConfluenceRow
            {
                Id = "", // optional — if you don’t know the Confluence row ID yet
                ExternalId = item.Id,
                Title = item.Title,
                LastModifiedUtc = item.LastModifiedUtc,
                Fields = new Dictionary<string, object>
                {
                    { "Status", item.Fields.TryGetValue("Status", out var status) ? status : "" },
                    { "Owner", item.Fields.TryGetValue("Owner", out var owner) ? owner : "" }
                }
            };
        }
    }
}
