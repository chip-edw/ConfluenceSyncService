using ConfluenceSyncService.Models;

namespace ConfluenceSyncService.Utilities
{
    public static class SyncMapper
    {
        public static ConfluenceRow MapToConfluenceRow(SharePointListItem item)
        {
            return new ConfluenceRow
            {
                ExternalId = item.Id,
                Title = item.Title,
                Fields = new Dictionary<string, object>
                {
                    { "Status", item.Status },
                    { "Owner", item.Owner }
                    // Add additional field mappings as needed
                }
            };
        }
    }
}
