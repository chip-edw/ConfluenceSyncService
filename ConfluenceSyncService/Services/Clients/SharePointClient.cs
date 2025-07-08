using ConfluenceSyncService.Models;
namespace ConfluenceSyncService.Services.Clients
{
    public class SharePointClient
    {
        public async Task<List<SharePointListItem>> GetAllListItemsAsync(string sitePath, string listName)
        {
            // TODO: Implement MS Graph API call to get SharePoint list items
            await Task.Delay(100); // Simulate async operation

            return new List<SharePointListItem>(); // Return empty list for now
        }
    }
}
