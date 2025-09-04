using ConfluenceSyncService.Dtos;
using ConfluenceSyncService.Models;
using ConfluenceSyncService.MSGraphAPI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ConfluenceSyncService.Services.Clients
{
    public class SharePointClient
    {
        private readonly HttpClient _httpClient;
        private readonly ConfidentialClientApp _confidentialClientApp;
        private readonly IConfiguration _configuration;
        private readonly Serilog.ILogger _logger;

        // Cache for list IDs keyed by "siteKey|displayName"
        private readonly ConcurrentDictionary<string, string> _listIdCache = new();

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

                var payload = new { fields };

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());
                request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request);

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

            // Extra debug logging for payload
            _logger.Information("Fields being sent to SharePoint:");
            foreach (var field in fields)
                _logger.Information("  {FieldName}: {FieldValue}", field.Key, field.Value);

            try
            {
                var listId = await GetListIdByNameAsync(siteId, listName);
                var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items/{itemId}/fields";

                using var request = new HttpRequestMessage(HttpMethod.Patch, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                var jsonPayload = JsonConvert.SerializeObject(fields, Formatting.Indented);
                _logger.Information("JSON payload being sent to SharePoint: {JsonPayload}", jsonPayload);

                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request);

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

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                using var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        return null;

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
                var url = "https://graph.microsoft.com/v1.0/me";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                using var response = await _httpClient.SendAsync(request);
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

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                using var response = await _httpClient.SendAsync(request);
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

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                using var response = await _httpClient.SendAsync(request);

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

            var utcSince = sinceUtc.Kind == DateTimeKind.Utc ? sinceUtc : sinceUtc.ToUniversalTime();

            _logger.Debug("=== CONFIGURATION DEBUG ===");
            _logger.Debug("MaxFallbackItems from config: {Val}", _configuration.GetValue<int>("SharePoint:MaxFallbackItems", -1));
            _logger.Debug("EnableFallbackFiltering from config: {Val}", _configuration.GetValue<bool>("SharePoint:EnableFallbackFiltering", false));
            _logger.Debug("SharePoint section exists: {Val}", _configuration.GetSection("SharePoint").Exists());

            var sharepointSection = _configuration.GetSection("SharePoint");
            _logger.Debug("Raw MaxFallbackItems value: '{Val}'", sharepointSection["MaxFallbackItems"]);
            _logger.Debug("Raw EnableFallbackFiltering value: '{Val}'", sharepointSection["EnableFallbackFiltering"]);
            _logger.Debug("=== END CONFIG DEBUG ===");

            try
            {
                var listId = await GetListIdByNameAsync(sitePath, listName);

                var filterDate = utcSince.ToString("M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture);
                var url = $"https://graph.microsoft.com/v1.0/sites/{sitePath}/lists/{listId}/items" +
                          $"?$expand=fields&$filter=fields/Modified ge '{filterDate}'";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                using var response = await _httpClient.SendAsync(request);

                _logger.Debug("Response status: {Status}", response.StatusCode);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.Error("Failed: {Error}", errorContent);

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

                _logger.Debug("Successfully retrieved {Count} items using date filter", results.Count);
                return results;
            }
            catch (Exception ex)
            {
                _logger.Error("Exception occurred: {Msg}", ex.Message);
                _logger.Debug("Exception type: {Type}", ex.GetType().Name);
                if (ex.InnerException != null)
                    _logger.Debug("Inner exception: {Msg}", ex.InnerException.Message);
                throw;
            }
        }
        #endregion

        #region MarkTaskCompleteAsync old
        public async Task<string> xMarkTaskCompleteAsync(string resourceId, CancellationToken ct)
        {
            // If your resourceId is already the SP itemId, use it directly.
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
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());
            req.Headers.TryAddWithoutValidation("If-Match", "*");

            using var res = await _httpClient.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(ct);
                _logger.Warning("SharePoint PATCH failed ({Code}): {Body}", (int)res.StatusCode, err);
                res.EnsureSuccessStatusCode();
            }

            return itemId;
        }

        #endregion

        #region GetRecentlyModifiedItemsWithoutFilterAsync (Fallback)

        private async Task<List<SharePointListItemDto>> GetRecentlyModifiedItemsWithoutFilterAsync(string sitePath, string listName, DateTime sinceUtc)
        {
            var listId = await GetListIdByNameAsync(sitePath, listName);

            var maxItems = _configuration.GetValue<int>("SharePoint:MaxFallbackItems", 100);
            _logger.Debug("Fallback: MaxFallbackItems = {Max}", maxItems);

            var url = $"https://graph.microsoft.com/v1.0/sites/{sitePath}/lists/{listId}/items" +
                      $"?$expand=fields&$orderby=lastModifiedDateTime desc&$top={maxItems}";

            _logger.Debug("Fallback URL: {Url}", url);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

            using var response = await _httpClient.SendAsync(request);
            _logger.Debug("Fallback response status: {Status}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.Debug("Fallback failed: {Error}", errorContent);
                throw new HttpRequestException($"Fallback method failed: {response.StatusCode} - {errorContent}");
            }

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);

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
                        _logger.Debug("Field: '{Name}' = '{Value}'", field.Name, field.Value);
                }
            }
            _logger.Debug("=== END FIELD DEBUG ===\n");

            var totalItemsReturned = json["value"]?.Count() ?? 0;
            _logger.Debug("Graph API returned {Count} items (requested max {Max})", totalItemsReturned, maxItems);

            var results = new List<SharePointListItemDto>();
            var totalProcessed = 0;

            foreach (var item in json["value"] ?? Enumerable.Empty<JToken>())
            {
                totalProcessed++;
                var id = item["id"]?.ToString();
                var modifiedStr = item["lastModifiedDateTime"]?.ToString();
                var modified = DateTime.TryParse(modifiedStr, out var parsed) ? parsed : DateTime.UtcNow;

                if (totalProcessed <= 5)
                    _logger.Debug("Processing item {N}: ID={Id}, Modified={Mod}", totalProcessed, id, modified);

                // Items ordered desc; break when we pass the cutoff
                if (modified < sinceUtc)
                {
                    _logger.Debug("Reached items older than {Since}, stopping at item {N}", sinceUtc, totalProcessed);
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

            _logger.Debug("Fallback: Found {Count} items modified since {Since} (processed {Processed} / returned {Returned})",
                results.Count, sinceUtc, totalProcessed, totalItemsReturned);
            return results;
        }

        #endregion

        #region GetListIdByNameAsync
        private async Task<string> GetListIdByNameAsync(string sitePathOrId, string listName)
        {
            var cacheKey = $"{sitePathOrId}|{listName}";

            if (_listIdCache.TryGetValue(cacheKey, out var cachedId))
            {
                _logger.Debug("Using cached list ID for '{ListName}': {Id}", listName, cachedId);
                return cachedId;
            }

            _logger.Debug("Cache miss - fetching list ID for '{ListName}'", listName);

            var url = $"https://graph.microsoft.com/v1.0/sites/{sitePathOrId}/lists";
            _logger.Debug("Attempting to get lists from: {Url}", url);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

            using var response = await _httpClient.SendAsync(request);

            _logger.Debug("Lists API response status: {Status}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.Warning("Lists API error response: {Error}", errorContent);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.Warning("Site not found with path/id '{Site}'. Trying alternative formats...", sitePathOrId);

                    if (sitePathOrId.Contains(":/"))
                    {
                        var altSitePath = sitePathOrId.Split(":/")[1];
                        _logger.Warning("Trying alternative site path: '{Alt}'", altSitePath);
                        return await GetListIdByNameWithPath(altSitePath, listName);
                    }

                    if (!sitePathOrId.StartsWith("root", StringComparison.OrdinalIgnoreCase))
                    {
                        var rootSitePath = $"root/sites/{sitePathOrId.Replace("/sites/", "")}";
                        _logger.Warning("Trying root site path: '{Alt}'", rootSitePath);
                        return await GetListIdByNameWithPath(rootSitePath, listName);
                    }
                }

                throw new HttpRequestException($"Failed to get lists from site '{sitePathOrId}': {response.StatusCode} - {errorContent}");
            }

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);

            _logger.Debug("Found {Count} lists in site", json["value"]?.Count() ?? 0);

            foreach (var list in json["value"] ?? Enumerable.Empty<JToken>())
            {
                var displayName = list["displayName"]?.ToString();
                var listId = list["id"]?.ToString();

                _logger.Debug("Found list: '{Display}' with ID: {Id}", displayName, listId);

                if (!string.IsNullOrEmpty(displayName) && !string.IsNullOrEmpty(listId))
                {
                    var key = $"{sitePathOrId}|{displayName}";
                    _listIdCache.TryAdd(key, listId);
                    _logger.Debug("Cached list ID: '{Display}' -> {Id}", displayName, listId);
                }
            }

            if (_listIdCache.TryGetValue(cacheKey, out var foundId))
                return foundId;

            var availableLists = json["value"]?.Select(l => l["displayName"]?.ToString()).Where(n => !string.IsNullOrEmpty(n));
            var listNames = string.Join(", ", availableLists ?? Enumerable.Empty<string>());

            throw new InvalidOperationException($"List '{listName}' not found in site '{sitePathOrId}'. Available lists: {listNames}");
        }
        #endregion

        #region GetListIdByNameWithPath
        private async Task<string> GetListIdByNameWithPath(string altSitePath, string listName)
        {
            var cacheKey = $"{altSitePath}|{listName}";

            if (_listIdCache.TryGetValue(cacheKey, out var cachedId))
                return cachedId;

            var url = $"https://graph.microsoft.com/v1.0/sites/{altSitePath}/lists";
            _logger.Warning("Trying alternative URL: {Url}", url);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

            using var response = await _httpClient.SendAsync(request);

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
                return foundId;

            throw new InvalidOperationException($"List '{listName}' not found in alternative site path '{altSitePath}'");
        }

        #endregion

        #region ResolveField
        private string ResolveField(string listDisplayName, string logicalName)
        {
            // SharePointFieldMappings : { "<ListDisplayName>": { "<LogicalName>": "<InternalName>" } }
            var mappings = _configuration.GetSection("SharePointFieldMappings");
            if (!mappings.Exists())
                throw new InvalidOperationException("SharePointFieldMappings section is missing in configuration.");

            var listSection = mappings
                .GetChildren()
                .FirstOrDefault(s => string.Equals(s.Key, listDisplayName, StringComparison.OrdinalIgnoreCase));

            if (listSection is null)
                throw new InvalidOperationException($"No field mapping block found for list '{listDisplayName}'.");

            var fieldEntry = listSection.GetChildren()
                .FirstOrDefault(s => string.Equals(s.Key, logicalName, StringComparison.OrdinalIgnoreCase));

            var internalName = fieldEntry?.Value;
            if (string.IsNullOrWhiteSpace(internalName))
                throw new InvalidOperationException($"No field mapping found for logical field '{logicalName}' in list '{listDisplayName}'.");

            return internalName;
        }

        #endregion

        #region MarkTaskCompleteAsync (current)
        public async Task<string> MarkTaskCompleteAsync(string resourceId, CancellationToken ct)
        {
            const string listDisplayName = "Phase Tasks & Metadata";
            var statusField = ResolveField(listDisplayName, "Status");
            var completedDateField = ResolveField(listDisplayName, "CompletedDate");

            var siteId = GetSupportSiteIdFromConfig();
            var listId = await GetListIdByNameAsync(siteId, listDisplayName);

            var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items/{resourceId}/fields";

            var completedIso = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture); // UTC ISO 8601
            var payload = new Dictionary<string, object>
            {
                [statusField] = "Completed",
                [completedDateField] = completedIso
            };

            using var req = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());
            req.Headers.TryAddWithoutValidation("If-Match", "*");

            using var res = await _httpClient.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(ct);
                _logger.Warning("SharePoint PATCH failed ({Code}): {Body}", (int)res.StatusCode, err);
                res.EnsureSuccessStatusCode();
            }

            return resourceId;
        }
        #endregion

        #region GetListIdAsync (by siteId + displayName)
        private async Task<string> GetListIdAsync(string siteId, string listDisplayName, CancellationToken ct)
        {
            var encodedListName = Uri.EscapeDataString(listDisplayName);
            var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists?$filter=displayName eq '{encodedListName}'";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

            using var res = await _httpClient.SendAsync(req, ct);

            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(ct);
                _logger.Warning("Failed to get listId for {ListName} ({Code}): {Body}", listDisplayName, (int)res.StatusCode, err);
                res.EnsureSuccessStatusCode();
            }

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var listId = doc.RootElement
                            .GetProperty("value")
                            .EnumerateArray()
                            .FirstOrDefault()
                            .GetProperty("id")
                            .GetString();

            if (string.IsNullOrWhiteSpace(listId))
                throw new InvalidOperationException($"List '{listDisplayName}' not found in site {siteId}.");

            return listId!;
        }
        #endregion

        #region Helpers (Site/List resolution from config)

        private string GetSupportSiteIdFromConfig()
        {
            // Prefer Sites[DisplayName='Support']:SiteId, else fall back to SharePoint:SiteId
            var supportSite = _configuration.GetSection("SharePoint")
                                           .GetSection("Sites").GetChildren()
                                           .FirstOrDefault(s => string.Equals(s["DisplayName"], "Support", StringComparison.OrdinalIgnoreCase));

            var siteId = supportSite?["SiteId"] ?? _configuration["SharePoint:SiteId"];
            if (string.IsNullOrWhiteSpace(siteId))
                throw new InvalidOperationException("SharePoint site id not found. Ensure SharePoint:Sites[DisplayName='Support']:SiteId or SharePoint:SiteId is configured.");
            return siteId;
        }

        private async Task<string> GetPhaseTasksListIdAsync(string siteId, CancellationToken ct)
        {
            const string listDisplayName = "Phase Tasks & Metadata";
            return await GetListIdByNameAsync(siteId, listDisplayName);
        }

        #endregion

        #region C2 APIs

        public sealed record StatusDue(string Status, DateTimeOffset? DueDateUtc);

        /// <summary>
        /// C2: Confirm by item id (only Status + DueDateUtc), using mapped internal names.
        /// </summary>
        public async Task<StatusDue?> GetTaskStatusAndDueUtcAsync(long spItemId, CancellationToken ct)
        {
            var siteId = GetSupportSiteIdFromConfig();
            var listId = await GetPhaseTasksListIdAsync(siteId, ct);

            const string listDisplayName = "Phase Tasks & Metadata";
            var statusField = ResolveField(listDisplayName, "Status");
            var dueField = ResolveField(listDisplayName, "DueDateUtc");

            var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items/{spItemId}?$select=id&$expand=fields($select={statusField},{dueField})";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());
            using var resp = await _httpClient.SendAsync(req, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("fields", out var fields))
                return new StatusDue("", null);

            var status = fields.TryGetProperty(statusField, out var sProp) ? (sProp.GetString() ?? "") : "";
            DateTimeOffset? due = null;

            if (fields.TryGetProperty(dueField, out var dProp) && dProp.ValueKind == JsonValueKind.String)
            {
                var s = dProp.GetString();
                if (!string.IsNullOrWhiteSpace(s) && DateTimeOffset.TryParse(s, out var parsed))
                    due = parsed.ToUniversalTime();
            }

            return new StatusDue(status, due);
        }

        /// <summary>
        /// C2: Write-through on chase (Important, ChaseCount++, NextChaseAtUtc), using mapped internal names.
        /// </summary>
        public async Task UpdateChaserFieldsAsync(long spItemId, bool important, bool incrementChase, DateTimeOffset nextChaseAtUtc, CancellationToken ct)
        {
            var siteId = GetSupportSiteIdFromConfig();
            var listId = await GetPhaseTasksListIdAsync(siteId, ct);

            const string listDisplayName = "Phase Tasks & Metadata";
            var chaseField = ResolveField(listDisplayName, "ChaseCount");
            var importantField = ResolveField(listDisplayName, "Important");
            var nextChaseField = ResolveField(listDisplayName, "NextChaseAtUtc");

            int? newChaseCount = null;

            if (incrementChase)
            {
                var getUrl = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items/{spItemId}?$select=id&$expand=fields($select={chaseField})";
                using var getReq = new HttpRequestMessage(HttpMethod.Get, getUrl);
                getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());
                using var getResp = await _httpClient.SendAsync(getReq, ct);
                getResp.EnsureSuccessStatusCode();

                using var s = await getResp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
                var current = 0;
                if (doc.RootElement.TryGetProperty("fields", out var f) &&
                    f.TryGetProperty(chaseField, out var cc) &&
                    cc.ValueKind == JsonValueKind.Number)
                {
                    current = cc.GetInt32();
                }
                newChaseCount = current + 1;
            }

            var patchUrl = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items/{spItemId}/fields";
            var payload = new Dictionary<string, object?>
            {
                [importantField] = important,
                [nextChaseField] = nextChaseAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
            };
            if (newChaseCount.HasValue)
                payload[chaseField] = newChaseCount.Value;

            using var patchReq = new HttpRequestMessage(new HttpMethod("PATCH"), patchUrl)
            {
                Content = System.Net.Http.Json.JsonContent.Create(payload)
            };
            patchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

            using var resp = await _httpClient.SendAsync(patchReq, ct);
            resp.EnsureSuccessStatusCode();
        }

        #endregion
    }
}
