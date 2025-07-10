using ConfluenceSyncService.Dtos;
using ConfluenceSyncService.MSGraphAPI;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace ConfluenceSyncService.Services.Clients
{
    public class SharePointClient
    {
        private readonly HttpClient _httpClient;
        private readonly ConfidentialClientApp _confidentialClientApp;
        private readonly IConfiguration _configuration;
        // Add a cache for list IDs
        private readonly ConcurrentDictionary<string, string> _listIdCache = new();

        public SharePointClient(HttpClient httpClient, ConfidentialClientApp confidentialClientApp, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _confidentialClientApp = confidentialClientApp;
            _configuration = configuration;
        }

        public async Task<List<SharePointListItemDto>> GetRecentlyModifiedItemsAsync(string sitePath, string listName, DateTime sinceUtc)
        {
            var results = new List<SharePointListItemDto>();

            // Ensure we're working with UTC and format properly for OData
            var utcSince = sinceUtc.Kind == DateTimeKind.Utc ? sinceUtc : sinceUtc.ToUniversalTime();

            Console.WriteLine($"=== CONFIGURATION DEBUG ===");
            Console.WriteLine($"MaxFallbackItems from config: {_configuration.GetValue<int>("SharePoint:MaxFallbackItems", -1)}");
            Console.WriteLine($"EnableFallbackFiltering from config: {_configuration.GetValue<bool>("SharePoint:EnableFallbackFiltering", false)}");
            Console.WriteLine($"SharePoint section exists: {_configuration.GetSection("SharePoint").Exists()}");

            var sharepointSection = _configuration.GetSection("SharePoint");
            Console.WriteLine($"Raw MaxFallbackItems value: '{sharepointSection["MaxFallbackItems"]}'");
            Console.WriteLine($"Raw EnableFallbackFiltering value: '{sharepointSection["EnableFallbackFiltering"]}'");
            Console.WriteLine($"=== END CONFIG DEBUG ===");

            try
            {
                // Step 1: Get the list ID by display name (with caching)
                var listId = await GetListIdByNameAsync(sitePath, listName);

                // Replace your attempts array with just the working solution:
                var filterDate = utcSince.ToString("M/d/yyyy h:mm:ss tt");
                var url = $"https://graph.microsoft.com/v1.0/sites/{sitePath}/lists/{listId}/items" +
                          $"?$expand=fields&$filter=fields/Modified ge '{filterDate}'";


                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                var response = await _httpClient.SendAsync(request);

                Console.WriteLine($"Response status: {response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"SUCCESS !");
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Failed: {errorContent}");
                }

                // If all attempts failed, use fallback
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine("All date filter attempts failed, using fallback method...");
                    return await GetRecentlyModifiedItemsWithoutFilterAsync(sitePath, listName, sinceUtc);
                }

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

                Console.WriteLine($"Successfully retrieved {results.Count} items using date filter");
                return results;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception occurred: {ex.Message}");
                Console.WriteLine($"Exception type: {ex.GetType().Name}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                }
                throw;
            }
        }


        // Fallback method - get all items and filter in memory (with configurable safety limits)
        private async Task<List<SharePointListItemDto>> GetRecentlyModifiedItemsWithoutFilterAsync(string sitePath, string listName, DateTime sinceUtc)
        {
            var listId = await GetListIdByNameAsync(sitePath, listName);

            // Get the limit from configuration with debugging
            var maxItems = _configuration.GetValue<int>("SharePoint:MaxFallbackItems", 100);
            Console.WriteLine($"DEBUG: Configuration MaxFallbackItems = {maxItems}");
            Console.WriteLine($"DEBUG: Configuration section exists = {_configuration.GetSection("SharePoint").Exists()}");

            // Debug individual config values
            var sharepointSection = _configuration.GetSection("SharePoint");
            Console.WriteLine($"DEBUG: SharePoint:MaxFallbackItems = {sharepointSection["MaxFallbackItems"]}");
            Console.WriteLine($"DEBUG: SharePoint:EnableFallbackFiltering = {sharepointSection["EnableFallbackFiltering"]}");
            Console.WriteLine($"DEBUG: SharePoint:Hostname = {sharepointSection["Hostname"]}");

            // Add safety limit and ordering to get most recent items first
            var url = $"https://graph.microsoft.com/v1.0/sites/{sitePath}/lists/{listId}/items" +
                      $"?$expand=fields&$orderby=lastModifiedDateTime desc&$top={maxItems}";

            Console.WriteLine($"Fallback: Getting recent items with safety limit of {maxItems} items (from config)");
            Console.WriteLine($"Fallback URL: {url}");

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

            var response = await _httpClient.SendAsync(request);
            Console.WriteLine($"Fallback response status: {response.StatusCode}");




            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Fallback failed: {errorContent}");
                throw new HttpRequestException($"Fallback method failed: {response.StatusCode} - {errorContent}");
            }

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);



            // In the fallback method, after getting the JSON response, add this:
            // Add this debugging section:
            Console.WriteLine("\n=== FIELD STRUCTURE DEBUG ===");
            var firstItem = json["value"]?.FirstOrDefault();
            if (firstItem != null)
            {
                Console.WriteLine("Raw item structure:");
                Console.WriteLine(firstItem.ToString());

                var fields = firstItem["fields"];
                if (fields != null)
                {
                    Console.WriteLine("\nAvailable fields:");
                    foreach (var field in fields.Children<JProperty>())
                    {
                        Console.WriteLine($"Field: '{field.Name}' = '{field.Value}'");
                    }
                }
            }
            Console.WriteLine("=== END FIELD DEBUG ===\n");

            var totalItemsReturned = json["value"]?.Count() ?? 0;
            Console.WriteLine($"Graph API returned {totalItemsReturned} items (requested max {maxItems})");

            var results = new List<SharePointListItemDto>();
            var totalProcessed = 0;

            foreach (var item in json["value"] ?? Enumerable.Empty<JToken>())
            {
                totalProcessed++;
                var id = item["id"]?.ToString();
                var modifiedStr = item["lastModifiedDateTime"]?.ToString();
                var modified = DateTime.TryParse(modifiedStr, out var parsed) ? parsed : DateTime.UtcNow;

                // Only log first 5 items to avoid spam
                if (totalProcessed <= 5)
                {
                    Console.WriteLine($"Processing item {totalProcessed}: ID={id}, Modified={modified}");
                }

                // Since items are ordered by lastModifiedDateTime desc, 
                // we can break early when we hit the cutoff date
                if (modified < sinceUtc)
                {
                    Console.WriteLine($"Reached items older than {sinceUtc}, stopping at item {totalProcessed}");
                    break;
                }

                var fields = item["fields"]?.ToObject<Dictionary<string, object>>() ?? new();

                results.Add(new SharePointListItemDto
                {
                    Id = id,
                    LastModifiedUtc = modified,
                    Title = fields.TryGetValue("Title", out var titleVal) ? titleVal?.ToString() : "",
                    Fields = fields
                });
            }

            Console.WriteLine($"Fallback method: Found {results.Count} items modified since {sinceUtc} (processed {totalProcessed} total items, max allowed: {maxItems}, API returned: {totalItemsReturned})");
            return results;
        }
        private async Task<string> GetListIdByNameAsync(string sitePath, string listName)
        {
            var cacheKey = $"{sitePath}|{listName}";

            // Check cache first
            if (_listIdCache.TryGetValue(cacheKey, out var cachedId))
            {
                Console.WriteLine($"Using cached list ID for '{listName}': {cachedId}");
                return cachedId;
            }

            Console.WriteLine($"Cache miss - fetching list ID for '{listName}'");

            var url = $"https://graph.microsoft.com/v1.0/sites/{sitePath}/lists";

            Console.WriteLine($"Attempting to get lists from: {url}");

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

            var response = await _httpClient.SendAsync(request);

            Console.WriteLine($"Lists API response status: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Lists API error response: {errorContent}");

                // Try alternative site path formats
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Console.WriteLine($"Site not found with path '{sitePath}'. Trying alternative formats...");

                    // Try without the protocol prefix if it exists
                    if (sitePath.Contains(":/"))
                    {
                        var altSitePath = sitePath.Split(":/")[1];
                        Console.WriteLine($"Trying alternative site path: '{altSitePath}'");
                        return await GetListIdByNameWithPath(altSitePath, listName);
                    }

                    // Try with root prefix if it doesn't exist
                    if (!sitePath.StartsWith("root"))
                    {
                        var rootSitePath = $"root/sites/{sitePath.Replace("/sites/", "")}";
                        Console.WriteLine($"Trying root site path: '{rootSitePath}'");
                        return await GetListIdByNameWithPath(rootSitePath, listName);
                    }
                }

                throw new HttpRequestException($"Failed to get lists from site '{sitePath}': {response.StatusCode} - {errorContent}");
            }

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);

            Console.WriteLine($"Found {json["value"]?.Count() ?? 0} lists in site");

            // Cache ALL lists from this site while we're here
            foreach (var list in json["value"] ?? Enumerable.Empty<JToken>())
            {
                var displayName = list["displayName"]?.ToString();
                var listId = list["id"]?.ToString();

                Console.WriteLine($"Found list: '{displayName}' with ID: {listId}");

                if (!string.IsNullOrEmpty(displayName) && !string.IsNullOrEmpty(listId))
                {
                    var key = $"{sitePath}|{displayName}";
                    _listIdCache.TryAdd(key, listId);
                    Console.WriteLine($"Cached list ID: '{displayName}' -> {listId}");
                }
            }

            // Now get the one we wanted
            if (_listIdCache.TryGetValue(cacheKey, out var foundId))
            {
                return foundId;
            }

            // List available lists for debugging
            var availableLists = json["value"]?.Select(l => l["displayName"]?.ToString()).Where(n => !string.IsNullOrEmpty(n));
            var listNames = string.Join(", ", availableLists ?? Enumerable.Empty<string>());

            throw new InvalidOperationException($"List '{listName}' not found in site '{sitePath}'. Available lists: {listNames}");
        }

        private async Task<string> GetListIdByNameWithPath(string altSitePath, string listName)
        {
            var cacheKey = $"{altSitePath}|{listName}";

            if (_listIdCache.TryGetValue(cacheKey, out var cachedId))
            {
                return cachedId;
            }

            var url = $"https://graph.microsoft.com/v1.0/sites/{altSitePath}/lists";
            Console.WriteLine($"Trying alternative URL: {url}");

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"Alternative site path '{altSitePath}' also failed: {response.StatusCode} - {errorContent}");
            }

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);

            foreach (var list in json["value"] ?? Enumerable.Empty<JToken>())
            {
                var displayName = list["displayName"]?.ToString();
                var listId = list["id"]?.ToString();

                if (!string.IsNullOrEmpty(displayName) && !string.IsNullOrEmpty(listId))
                {
                    var key = $"{altSitePath}|{displayName}";
                    _listIdCache.TryAdd(key, listId);
                }
            }

            if (_listIdCache.TryGetValue(cacheKey, out var foundId))
            {
                return foundId;
            }

            throw new InvalidOperationException($"List '{listName}' not found in alternative site path '{altSitePath}'");
        }

        public async Task<List<SharePointListItemDto>> GetAllListItemsAsync(string siteId, string listId)
        {
            var results = new List<SharePointListItemDto>();

            var encodedSiteId = Uri.EscapeDataString(siteId);
            var encodedListId = Uri.EscapeDataString(listId);

            var url = $"https://graph.microsoft.com/v1.0/sites/{encodedSiteId}/lists/{encodedListId}/items?$expand=fields";

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