using ConfluenceSyncService.Dtos;
using ConfluenceSyncService.MSGraphAPI;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;

namespace ConfluenceSyncService.Services.Clients
{
    public class SharePointClient
    {
        private readonly HttpClient _httpClient;
        private readonly ConfidentialClientApp _confidentialClientApp;

        public SharePointClient(HttpClient httpClient, ConfidentialClientApp confidentialClientApp)
        {
            _httpClient = httpClient;
            _confidentialClientApp = confidentialClientApp;
        }


        public async Task<List<SharePointListItemDto>> GetRecentlyModifiedItemsAsync(string siteId, string listId, DateTime sinceUtc,
            HttpClient httpClient)
        {
            var results = new List<SharePointListItemDto>();
            var sinceIso = sinceUtc.ToString("o"); // ISO 8601 format

            var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items" +
                      $"?$expand=fields&$filter=lastModifiedDateTime gt {sinceIso}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);

            foreach (var item in json["value"] ?? Enumerable.Empty<JToken>())
            {
                var id = item["id"]?.ToString();
                var modifiedStr = item["lastModifiedDateTime"]?.ToString();
                var modified = DateTime.TryParse(modifiedStr, out var parsed) ? parsed : DateTime.UtcNow;

                var fields = item["fields"]?.ToObject<Dictionary<string, object>>() ?? new();

                results.Add(new SharePointListItemDto
                {
                    Id = id,
                    LastModifiedUtc = modified,
                    Title = fields.TryGetValue("Title", out var titleVal) ? titleVal?.ToString() : "",
                    Fields = fields
                });
            }

            return results;
        }

        public async Task<List<SharePointListItemDto>> GetAllListItemsAsync(string siteId, string listId)
        {
            var results = new List<SharePointListItemDto>();

            var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items?$expand=fields";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);

            foreach (var item in json["value"] ?? Enumerable.Empty<JToken>())
            {
                var id = item["id"]?.ToString();
                var modifiedStr = item["lastModifiedDateTime"]?.ToString();
                var modified = DateTime.TryParse(modifiedStr, out var parsed) ? parsed : DateTime.UtcNow;

                var fields = item["fields"]?.ToObject<Dictionary<string, object>>() ?? new();

                results.Add(new SharePointListItemDto
                {
                    Id = id,
                    LastModifiedUtc = modified,
                    Title = fields.TryGetValue("Title", out var titleVal) ? titleVal?.ToString() : "",
                    Fields = fields
                });
            }

            return results;
        }


    }
}
