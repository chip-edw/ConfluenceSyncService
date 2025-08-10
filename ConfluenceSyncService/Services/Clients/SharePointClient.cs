using ConfluenceSyncService.Dtos;
using ConfluenceSyncService.Models;
using ConfluenceSyncService.MSGraphAPI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;

namespace ConfluenceSyncService.Services.Clients
{
    public class SharePointClient
    {
        private readonly HttpClient _httpClient;
        private readonly ConfidentialClientApp _confidentialClientApp;
        private readonly IConfiguration _configuration;
        private readonly Serilog.ILogger _logger;
        private readonly ConcurrentDictionary<string, string> _listIdCache = new();         // Add a cache for list IDs

        public SharePointClient(HttpClient httpClient, ConfidentialClientApp confidentialClientApp, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _confidentialClientApp = confidentialClientApp;
            _configuration = configuration;
            _logger = Log.ForContext<SharePointClient>();
        }

        #region Table Sync Methods

        /// <summary>
        /// Creates a new list item in SharePoint
        /// </summary>
        public async Task<string> CreateListItemAsync(string siteId, string listName, Dictionary<string, object> fields)
        {
            _logger.Information("Creating new SharePoint list item in {ListName}", listName);

            try
            {
                var listId = await GetListIdByNameAsync(siteId, listName);
                var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items";

                var payload = new
                {
                    fields = fields
                };

                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());
                request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.Error("Failed to create SharePoint item: {StatusCode} - {Error}", response.StatusCode, errorContent);
                    throw new HttpRequestException($"Failed to create SharePoint item: {response.StatusCode} - {errorContent}");
                }

                var content = await response.Content.ReadAsStringAsync();
                var json = JObject.Parse(content);
                var itemId = json["id"]?.ToString();

                _logger.Information("Successfully created SharePoint item with ID: {ItemId}", itemId);
                return itemId ?? throw new InvalidOperationException("Created item ID was null");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error creating SharePoint list item");
                throw;
            }
        }

        /// <summary>
        /// Updates an existing list item in SharePoint
        /// </summary>
        public async Task UpdateListItemAsync(string siteId, string listName, string itemId, Dictionary<string, object> fields)
        {
            _logger.Information("Updating SharePoint list item {ItemId} in {ListName}", itemId, listName);

            // ADD THIS DEBUG LOGGING
            _logger.Information("Fields being sent to SharePoint:");
            foreach (var field in fields)
            {
                _logger.Information("  {FieldName}: {FieldValue}", field.Key, field.Value);
            }

            try
            {
                var listId = await GetListIdByNameAsync(siteId, listName);
                var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items/{itemId}/fields";

                var request = new HttpRequestMessage(HttpMethod.Patch, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                // LOG THE JSON BEING SENT
                var jsonPayload = JsonConvert.SerializeObject(fields, Formatting.Indented);
                _logger.Information("JSON payload being sent to SharePoint: {JsonPayload}", jsonPayload);

                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.Error("Failed to update SharePoint item {ItemId}: {StatusCode} - {Error}", itemId, response.StatusCode, errorContent);
                    throw new HttpRequestException($"Failed to update SharePoint item {itemId}: {response.StatusCode} - {errorContent}");
                }

                _logger.Information("Successfully updated SharePoint item {ItemId}", itemId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error updating SharePoint list item {ItemId}", itemId);
                throw;
            }
        }

        /// <summary>
        /// Gets a specific list item by ID
        /// </summary>
        public async Task<SharePointListItemDto?> GetListItemAsync(string siteId, string listName, string itemId)
        {
            try
            {
                var listId = await GetListIdByNameAsync(siteId, listName);
                var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items/{itemId}?$expand=fields";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        return null;
                    }

                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"Failed to get SharePoint item {itemId}: {response.StatusCode} - {errorContent}");
                }

                var content = await response.Content.ReadAsStringAsync();
                var json = JObject.Parse(content);

                var id = json["id"]?.ToString();
                var modifiedStr = json["lastModifiedDateTime"]?.ToString();
                var modified = DateTime.TryParse(modifiedStr, out var parsed) ? parsed : DateTime.UtcNow;
                var fields = json["fields"]?.ToObject<Dictionary<string, object>>() ?? new();

                return new SharePointListItemDto
                {
                    Id = id,
                    LastModifiedUtc = modified,
                    Title = fields.TryGetValue("Title", out var titleVal) ? titleVal?.ToString() : "",
                    Fields = fields
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error getting SharePoint list item {ItemId}", itemId);
                throw;
            }
        }

        /// <summary>
        /// Token validation method for orchestrator
        /// </summary>
        public async Task<bool> ValidateTokenAsync()
        {
            try
            {
                // Simple token validation - try to make a basic Graph API call
                var url = "https://graph.microsoft.com/v1.0/me";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Token refresh method for orchestrator
        /// </summary>
        public async Task RefreshTokenAsync()
        {
            // Token refresh is handled by ConfidentialClientApp.GetAccessToken()
            // It automatically refreshes if needed
            await _confidentialClientApp.GetAccessToken();
        }

        #endregion

        #region Updated GetAllListItemsAsync for new model

        public async Task<List<SharePointListItem>> GetAllListItemsAsync(string siteId, string listName)
        {
            var results = new List<SharePointListItem>();

            try
            {
                var listId = await GetListIdByNameAsync(siteId, listName);
                var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items?$expand=fields";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                var json = JObject.Parse(content);

                foreach (var item in json["value"] ?? Enumerable.Empty<JToken>())
                {
                    var id = item["id"]?.ToString() ?? "";
                    var modifiedStr = item["lastModifiedDateTime"]?.ToString();
                    var modified = DateTime.TryParse(modifiedStr, out var parsed) ? parsed : DateTime.UtcNow;

                    var fields = item["fields"]?.ToObject<Dictionary<string, object>>() ?? new();

                    results.Add(new SharePointListItem
                    {
                        Id = id,
                        Title = fields.TryGetValue("Title", out var titleVal) ? titleVal?.ToString() ?? "" : "",
                        LastModifiedUtc = modified,
                        Fields = fields
                    });
                }

                _logger.Information("Retrieved {Count} items from SharePoint list {ListName}", results.Count, listName);
                return results;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error getting all list items from {ListName}", listName);
                throw;
            }
        }

        #endregion

        #region GetListFieldsAsync
        /// <summary>
        /// Gets the SharePoint list schema to discover actual field names
        /// </summary>
        public async Task<Dictionary<string, string>> GetListFieldsAsync(string siteId, string listName)
        {
            try
            {
                var listId = await GetListIdByNameAsync(siteId, listName);
                var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/columns";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.Error("Failed to get list fields: {StatusCode} - {Error}", response.StatusCode, errorContent);
                    throw new HttpRequestException($"Failed to get list fields: {response.StatusCode} - {errorContent}");
                }

                var content = await response.Content.ReadAsStringAsync();
                var json = JObject.Parse(content);

                var fieldMap = new Dictionary<string, string>();

                foreach (var field in json["value"] ?? Enumerable.Empty<JToken>())
                {
                    var displayName = field["displayName"]?.ToString();
                    var name = field["name"]?.ToString();

                    if (!string.IsNullOrEmpty(displayName) && !string.IsNullOrEmpty(name))
                    {
                        fieldMap[displayName] = name;
                        _logger.Information("SharePoint Field: '{DisplayName}' -> '{InternalName}'", displayName, name);
                    }
                }

                return fieldMap;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error getting SharePoint list fields");
                throw;
            }
        }
        #endregion

        #region GetRecentlyModifiedItemsAsync
        public async Task<List<SharePointListItemDto>> GetRecentlyModifiedItemsAsync(string sitePath, string listName, DateTime sinceUtc)
        {
            var results = new List<SharePointListItemDto>();

            // Ensure we're working with UTC and format properly for OData
            var utcSince = sinceUtc.Kind == DateTimeKind.Utc ? sinceUtc : sinceUtc.ToUniversalTime();

            _logger.Debug($"=== CONFIGURATION DEBUG ===");
            _logger.Debug($"MaxFallbackItems from config: {_configuration.GetValue<int>("SharePoint:MaxFallbackItems", -1)}");
            _logger.Debug($"EnableFallbackFiltering from config: {_configuration.GetValue<bool>("SharePoint:EnableFallbackFiltering", false)}");
            _logger.Debug($"SharePoint section exists: {_configuration.GetSection("SharePoint").Exists()}");

            var sharepointSection = _configuration.GetSection("SharePoint");
            _logger.Debug($"Raw MaxFallbackItems value: '{sharepointSection["MaxFallbackItems"]}'");
            _logger.Debug($"Raw EnableFallbackFiltering value: '{sharepointSection["EnableFallbackFiltering"]}'");
            _logger.Debug($"=== END CONFIG DEBUG ===");

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

                _logger.Debug($"Response status: {response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    _logger.Debug($"SUCCESS !");
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.Error($"Failed: {errorContent}");
                }

                // If all attempts failed, use fallback
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warning("All date filter attempts failed, using fallback method...");
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

                _logger.Debug($"Successfully retrieved {results.Count} items using date filter");
                return results;
            }
            catch (Exception ex)
            {
                _logger.Error($"Exception occurred: {ex.Message}");
                _logger.Debug($"Exception type: {ex.GetType().Name}");
                if (ex.InnerException != null)
                {
                    _logger.Debug($"Inner exception: {ex.InnerException.Message}");
                }
                throw;
            }
        }
        #endregion


        #region MarkTaskCompleteAsync
        public async Task<string> MarkTaskCompleteAsync(string resourceId, CancellationToken ct)
        {
            // If your resourceId is already the SP itemId, use it directly.
            // If it is a business key, look up itemId first (omitted for brevity).
            var itemId = resourceId;

            var siteId = _configuration["SharePoint:SiteId"]!;
            var listId = _configuration["SharePoint:ListId"]!;
            var statusField = _configuration["SharePoint:StatusField"] ?? "Status";

            var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items/{itemId}/fields";
            var body = new Dictionary<string, object> { [statusField] = "Completed" };

            using var req = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("If-Match", "*");

            var res = await _httpClient.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(ct);
                _logger.Warning("SharePoint PATCH failed ({Code}): {Body}", (int)res.StatusCode, err);
                res.EnsureSuccessStatusCode();
            }

            return itemId;
        }

        #endregion

        // Fallback method - get all items and filter in memory (with configurable safety limits)
        private async Task<List<SharePointListItemDto>> GetRecentlyModifiedItemsWithoutFilterAsync(string sitePath, string listName, DateTime sinceUtc)
        {
            var listId = await GetListIdByNameAsync(sitePath, listName);

            // Get the limit from configuration with debugging
            var maxItems = _configuration.GetValue<int>("SharePoint:MaxFallbackItems", 100);
            _logger.Debug($"DEBUG: Configuration MaxFallbackItems = {maxItems}");
            _logger.Debug($"DEBUG: Configuration section exists = {_configuration.GetSection("SharePoint").Exists()}");

            // Debug individual config values
            var sharepointSection = _configuration.GetSection("SharePoint");
            _logger.Debug($"DEBUG: SharePoint:MaxFallbackItems = {sharepointSection["MaxFallbackItems"]}");
            _logger.Debug($"DEBUG: SharePoint:EnableFallbackFiltering = {sharepointSection["EnableFallbackFiltering"]}");
            _logger.Debug($"DEBUG: SharePoint:Hostname = {sharepointSection["Hostname"]}");

            // Add safety limit and ordering to get most recent items first
            var url = $"https://graph.microsoft.com/v1.0/sites/{sitePath}/lists/{listId}/items" +
                      $"?$expand=fields&$orderby=lastModifiedDateTime desc&$top={maxItems}";

            _logger.Debug($"Fallback: Getting recent items with safety limit of {maxItems} items (from config)");
            _logger.Debug($"Fallback URL: {url}");

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

            var response = await _httpClient.SendAsync(request);
            _logger.Debug($"Fallback response status: {response.StatusCode}");




            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.Debug($"Fallback failed: {errorContent}");
                throw new HttpRequestException($"Fallback method failed: {response.StatusCode} - {errorContent}");
            }

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);



            // In the fallback method, after getting the JSON response, add this:
            // Add this debugging section:
            _logger.Debug("\n=== FIELD STRUCTURE DEBUG ===");
            var firstItem = json["value"]?.FirstOrDefault();
            if (firstItem != null)
            {
                _logger.Debug("Raw item structure:");
                _logger.Debug(firstItem.ToString());

                var fields = firstItem["fields"];
                if (fields != null)
                {
                    _logger.Debug("\nAvailable fields:");
                    foreach (var field in fields.Children<JProperty>())
                    {
                        _logger.Debug($"Field: '{field.Name}' = '{field.Value}'");
                    }
                }
            }
            _logger.Debug("=== END FIELD DEBUG ===\n");

            var totalItemsReturned = json["value"]?.Count() ?? 0;
            _logger.Debug($"Graph API returned {totalItemsReturned} items (requested max {maxItems})");

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
                    _logger.Debug($"Processing item {totalProcessed}: ID={id}, Modified={modified}");
                }

                // Since items are ordered by lastModifiedDateTime desc, 
                // we can break early when we hit the cutoff date
                if (modified < sinceUtc)
                {
                    _logger.Debug($"Reached items older than {sinceUtc}, stopping at item {totalProcessed}");
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

            _logger.Debug($"Fallback method: Found {results.Count} items modified since {sinceUtc} (processed {totalProcessed} total items, max allowed: {maxItems}, API returned: {totalItemsReturned})");
            return results;
        }
        private async Task<string> GetListIdByNameAsync(string sitePath, string listName)
        {
            var cacheKey = $"{sitePath}|{listName}";

            // Check cache first
            if (_listIdCache.TryGetValue(cacheKey, out var cachedId))
            {
                _logger.Debug($"Using cached list ID for '{listName}': {cachedId}");
                return cachedId;
            }

            _logger.Debug($"Cache miss - fetching list ID for '{listName}'");

            var url = $"https://graph.microsoft.com/v1.0/sites/{sitePath}/lists";

            _logger.Debug($"Attempting to get lists from: {url}");

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

            var response = await _httpClient.SendAsync(request);

            _logger.Debug($"Lists API response status: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.Warning($"Lists API error response: {errorContent}");

                // Try alternative site path formats
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.Warning($"Site not found with path '{sitePath}'. Trying alternative formats...");

                    // Try without the protocol prefix if it exists
                    if (sitePath.Contains(":/"))
                    {
                        var altSitePath = sitePath.Split(":/")[1];
                        _logger.Warning($"Trying alternative site path: '{altSitePath}'");
                        return await GetListIdByNameWithPath(altSitePath, listName);
                    }

                    // Try with root prefix if it doesn't exist
                    if (!sitePath.StartsWith("root"))
                    {
                        var rootSitePath = $"root/sites/{sitePath.Replace("/sites/", "")}";
                        _logger.Warning($"Trying root site path: '{rootSitePath}'");
                        return await GetListIdByNameWithPath(rootSitePath, listName);
                    }
                }

                throw new HttpRequestException($"Failed to get lists from site '{sitePath}': {response.StatusCode} - {errorContent}");
            }

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);

            _logger.Debug($"Found {json["value"]?.Count() ?? 0} lists in site");

            // Cache ALL lists from this site while we're here
            foreach (var list in json["value"] ?? Enumerable.Empty<JToken>())
            {
                var displayName = list["displayName"]?.ToString();
                var listId = list["id"]?.ToString();

                _logger.Debug($"Found list: '{displayName}' with ID: {listId}");

                if (!string.IsNullOrEmpty(displayName) && !string.IsNullOrEmpty(listId))
                {
                    var key = $"{sitePath}|{displayName}";
                    _listIdCache.TryAdd(key, listId);
                    _logger.Debug($"Cached list ID: '{displayName}' -> {listId}");
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
            _logger.Warning($"Trying alternative URL: {url}");

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

    }
}