using ConfluenceSyncService.MSGraphAPI;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Serilog;
using System.Net.Http.Headers;
using System.Text;

namespace ConfluenceSyncService.SharePoint
{
    // Configuration models to match your appsettings.json structure
    public class SiteConfig
    {
        public string DisplayName { get; set; } = "";
        public string SitePath { get; set; } = "";
        public string SiteId { get; set; } = "";
        public List<ListConfig> Lists { get; set; } = new();
    }

    public class ListConfig
    {
        public string DisplayName { get; set; } = "";
    }

    public interface ISharePointTaskUpdater
    {
        Task<bool> MarkCompletedAsync(string listId, string itemId, string ackedBy, string? ackedByActual, CancellationToken ct);
        Task<bool> StampNotifiedAsync(string listId, string itemId, DateTime utc, CancellationToken ct);
        Task<bool> StampChaseAsync(string listId, string itemId, int chaseCount, DateTime nextChaseUtc, bool important, CancellationToken ct);
    }

    public sealed class SharePointTaskUpdater : ISharePointTaskUpdater
    {
        private readonly HttpClient _http;
        private readonly SharePointFieldMappingsOptions _map;
        private readonly ConfidentialClientApp _confidentialClientApp;
        private readonly IConfiguration _configuration;
        private readonly Serilog.ILogger _logger;

        public SharePointTaskUpdater(HttpClient http, IOptions<SharePointFieldMappingsOptions> map, ConfidentialClientApp confidentialClientApp, IConfiguration configuration)
        {
            _http = http;
            _map = map.Value;
            _confidentialClientApp = confidentialClientApp;
            _configuration = configuration;
            _logger = Log.ForContext<SharePointTaskUpdater>();
        }

        public async Task<bool> MarkCompletedAsync(string listId, string itemId, string ackedBy, string? ackedByActual, CancellationToken ct)
        {
            // If listId is empty, get it from configuration
            string actualListId = listId;
            string siteId = null;

            if (string.IsNullOrWhiteSpace(actualListId))
            {
                // Get the site configuration (matching your other clients)
                var sites = _configuration.GetSection("SharePoint:Sites").Get<List<SiteConfig>>();
                var supportSite = sites?.FirstOrDefault(s => s.DisplayName == "Support");

                if (supportSite == null)
                {
                    _logger.Error("Support site configuration not found in SharePoint:Sites");
                    return false;
                }

                siteId = supportSite.SiteId;

                // Use the "Phase Tasks & Metadata" list from the Support site
                var phaseTasksList = supportSite.Lists?.FirstOrDefault(l => l.DisplayName == "Phase Tasks & Metadata");
                if (phaseTasksList == null)
                {
                    _logger.Error("Phase Tasks & Metadata list not found in Support site configuration");
                    return false;
                }

                _logger.Information("Looking up list ID for '{ListName}' in site '{SiteId}'", phaseTasksList.DisplayName, siteId);
                actualListId = await GetListIdAsync(siteId, phaseTasksList.DisplayName, ct);
            }

            // The itemId parameter is actually the TaskId (business key), not SharePoint item ID
            // We need to find the SharePoint list item where TaskId field equals itemId
            _logger.Information("Looking up SharePoint item ID for TaskId '{TaskId}'", itemId);
            var sharePointItemId = await FindSharePointItemIdByTaskIdAsync(siteId, actualListId, itemId, ct);

            if (string.IsNullOrWhiteSpace(sharePointItemId))
            {
                _logger.Error("No SharePoint item found with TaskId '{TaskId}'", itemId);
                return false;
            }

            // Debug the input parameters first
            _logger.Information("Input parameters: ackedBy='{AckedBy}', ackedByActual='{AckedByActual}' (is null: {IsNull})",
                ackedBy ?? "NULL", ackedByActual ?? "NULL", ackedByActual == null);

            var fields = new Dictionary<string, object?>
            {
                [_map.Get("Status")] = "Completed",
                [_map.Get("CompletedDate")] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), // ISO 8601 format for SharePoint
                [_map.Get("AckedBy")] = ackedBy ?? "unknown"
            };

            // Only add AckedByActual if it has a non-null, non-empty value
            if (ackedByActual != null && !string.IsNullOrWhiteSpace(ackedByActual))
            {
                fields[_map.Get("AckedByActual")] = ackedByActual;
                _logger.Information("Added AckedByActual field with value: '{Value}'", ackedByActual);
            }
            else
            {
                _logger.Information("Skipping AckedByActual field (null or empty)");
            }

            // Log the field mappings and values being used
            _logger.Information("Final fields dictionary has {Count} entries:", fields.Count);
            foreach (var field in fields)
            {
                _logger.Information("  {FieldName}: '{FieldValue}' (type: {ValueType})",
                    field.Key, field.Value, field.Value?.GetType().Name ?? "null");
            }

            return await PatchFieldsAsync(siteId, actualListId, sharePointItemId, fields, ct);
        }

        public Task<bool> StampNotifiedAsync(string listId, string itemId, DateTime utc, CancellationToken ct)
            => PatchFieldsAsync(null, listId, itemId, new()
            {
                [_map.Get("NotifiedAtUtc")] = utc
            }, ct);

        public Task<bool> StampChaseAsync(string listId, string itemId, int chaseCount, DateTime nextChaseUtc, bool important, CancellationToken ct)
            => PatchFieldsAsync(null, listId, itemId, new()
            {
                [_map.Get("ChaseCount")] = chaseCount,
                [_map.Get("NextChaseAtUtc")] = nextChaseUtc,
                [_map.Get("Important")] = important
            }, ct);

        private async Task<bool> PatchFieldsAsync(string? siteId, string listId, string itemId, Dictionary<string, object?> fields, CancellationToken ct)
        {
            // Use provided siteId, or fall back to getting it from config
            if (string.IsNullOrWhiteSpace(siteId))
            {
                var sites = _configuration.GetSection("SharePoint:Sites").Get<List<SiteConfig>>();
                var supportSite = sites?.FirstOrDefault(s => s.DisplayName == "Support");

                if (supportSite == null)
                {
                    _logger.Error("Support site configuration not found in SharePoint:Sites for PatchFields");
                    return false;
                }

                siteId = supportSite.SiteId;
            }

            var url = $"/v1.0/sites/{siteId}/lists/{listId}/items/{itemId}/fields";

            _logger.Information("SharePoint PATCH attempt: URL={Url}, SiteId={SiteId}, ListId={ListId}, ItemId={ItemId}", url, siteId, listId, itemId);
            _logger.Information("SharePoint PATCH fields: {@Fields}", fields);
            _logger.Information("HttpClient BaseAddress: {BaseAddress}", _http.BaseAddress);

            using var request = new HttpRequestMessage(HttpMethod.Patch, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

            // CRITICAL: Add the If-Match header that SharePoint requires for updates
            request.Headers.TryAddWithoutValidation("If-Match", "*");

            // Use the same JSON serialization approach as the working SharePointClient
            var jsonPayload = JsonConvert.SerializeObject(fields, Formatting.Indented);
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(request, ct);

            _logger.Information("SharePoint PATCH response: StatusCode={StatusCode}", resp.StatusCode);

            if (!resp.IsSuccessStatusCode)
            {
                var errorContent = await resp.Content.ReadAsStringAsync(ct);
                _logger.Error("SharePoint PATCH failed: StatusCode={StatusCode}, Error={Error}", resp.StatusCode, errorContent);
            }
            else
            {
                var successContent = await resp.Content.ReadAsStringAsync(ct);
                _logger.Information("SharePoint PATCH success: {Content}", successContent);
            }

            return resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotModified;
        }

        private async Task<string> GetListIdAsync(string siteId, string listDisplayName, CancellationToken ct)
        {
            // Graph: GET /sites/{siteId}/lists?$filter=displayName eq '{listDisplayName}'
            var encodedListName = Uri.EscapeDataString(listDisplayName);
            var url = $"/v1.0/sites/{siteId}/lists?$filter=displayName eq '{encodedListName}'";

            _logger.Information("Getting list ID for '{ListName}' from site '{SiteId}'", listDisplayName, siteId);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

            var res = await _http.SendAsync(req, ct);

            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(ct);
                _logger.Error("Failed to get listId for {ListName} ({Code}): {Body}", listDisplayName, (int)res.StatusCode, err);
                throw new HttpRequestException($"Failed to get listId for {listDisplayName}: {res.StatusCode} - {err}");
            }

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            var listId = doc.RootElement
                            .GetProperty("value")
                            .EnumerateArray()
                            .FirstOrDefault()
                            .GetProperty("id")
                            .GetString();

            if (string.IsNullOrWhiteSpace(listId))
                throw new InvalidOperationException($"List '{listDisplayName}' not found in site {siteId}.");

            _logger.Information("Found list ID '{ListId}' for list '{ListName}'", listId, listDisplayName);
            return listId;
        }

        private async Task<string?> FindSharePointItemIdByTaskIdAsync(string siteId, string listId, string taskId, CancellationToken ct)
        {
            // Search for the SharePoint list item where the TaskId field equals the given taskId
            var taskIdFieldName = _map.Get("TaskId"); // Get the actual field name for TaskId
            var encodedTaskId = Uri.EscapeDataString(taskId);

            _logger.Information("TaskId field mapping: logical 'TaskId' -> actual field '{TaskIdFieldName}'", taskIdFieldName);

            // Use the correct syntax for filtering on custom fields in SharePoint
            var url = $"/v1.0/sites/{siteId}/lists/{listId}/items?$filter=fields/{taskIdFieldName} eq '{encodedTaskId}'&$select=id&$expand=fields";

            _logger.Information("Searching for SharePoint item with TaskId '{TaskId}' using filter: fields/{TaskIdFieldName} eq '{TaskId}'", taskId, taskIdFieldName, taskId);
            _logger.Information("Full search URL: {Url}", url);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

            // Add the header to allow filtering on non-indexed fields
            req.Headers.Add("Prefer", "HonorNonIndexedQueriesWarningMayFailRandomly");

            var res = await _http.SendAsync(req, ct);

            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(ct);
                _logger.Error("Failed to search for TaskId {TaskId} ({Code}): {Body}", taskId, (int)res.StatusCode, err);
                return null;
            }

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            var items = doc.RootElement.GetProperty("value");
            if (items.GetArrayLength() == 0)
            {
                _logger.Warning("No SharePoint item found with TaskId '{TaskId}'", taskId);
                return null;
            }

            if (items.GetArrayLength() > 1)
            {
                _logger.Warning("Multiple SharePoint items found with TaskId '{TaskId}', using the first one", taskId);
            }

            var sharePointItemId = items.EnumerateArray().First().GetProperty("id").GetString();
            _logger.Information("Found SharePoint item ID '{SharePointItemId}' for TaskId '{TaskId}'", sharePointItemId, taskId);

            return sharePointItemId;
        }

        private async Task LogSampleItemFieldsAsync(string siteId, string listId, CancellationToken ct)
        {
            try
            {
                var url = $"/v1.0/sites/{siteId}/lists/{listId}/items?$top=3&$expand=fields";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                var res = await _http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    var json = await res.Content.ReadAsStringAsync(ct);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);

                    var items = doc.RootElement.GetProperty("value");
                    _logger.Information("Sample SharePoint items field structure:");

                    foreach (var item in items.EnumerateArray().Take(2))
                    {
                        var id = item.GetProperty("id").GetString();
                        var fields = item.GetProperty("fields");

                        _logger.Information("Item {Id} fields:", id);
                        foreach (var field in fields.EnumerateObject())
                        {
                            _logger.Information("  Field: '{FieldName}' = '{FieldValue}'", field.Name, field.Value);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get sample items for field inspection");
            }
        }

        private async Task LogExistingTaskIdsAsync(string siteId, string listId, CancellationToken ct)
        {
            try
            {
                var taskIdFieldName = _map.Get("TaskId");
                var url = $"/v1.0/sites/{siteId}/lists/{listId}/items?$top=10&$expand=fields&$select=id,fields";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                var res = await _http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    var json = await res.Content.ReadAsStringAsync(ct);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);

                    var items = doc.RootElement.GetProperty("value");
                    _logger.Information("Existing TaskId values in SharePoint list:");

                    foreach (var item in items.EnumerateArray().Take(10))
                    {
                        var id = item.GetProperty("id").GetString();
                        var fields = item.GetProperty("fields");

                        if (fields.TryGetProperty(taskIdFieldName, out var taskIdValue))
                        {
                            _logger.Information("SharePoint Item {Id}: TaskId = '{TaskId}'", id, taskIdValue);
                        }
                        else
                        {
                            _logger.Information("SharePoint Item {Id}: No '{TaskIdFieldName}' field found", id, taskIdFieldName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get existing TaskIds for inspection");
            }
        }
    }
}
