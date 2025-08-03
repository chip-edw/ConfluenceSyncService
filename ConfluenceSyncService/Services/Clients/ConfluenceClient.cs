using ConfluenceSyncService.Auth;
using ConfluenceSyncService.Common.Secrets;
using ConfluenceSyncService.Dtos;
using ConfluenceSyncService.Models;
using Microsoft.Kiota.Abstractions.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System.Net.Http.Headers;
using System.Text;

namespace ConfluenceSyncService.Services.Clients
{
    public class ConfluenceClient
    {
        private readonly HttpClient _httpClient;
        private readonly Serilog.ILogger _logger;
        private readonly IConfiguration _configuration;
        private readonly IConfluenceAuthClient _authClient;
        private readonly ConfluenceColorMappings _colorMappings;
        private readonly ISecretsProvider _secretsProvider;

        public ConfluenceClient(HttpClient httpClient, IConfiguration configuration, ISecretsProvider secretsProvider)
        {
            _httpClient = httpClient;
            _logger = Log.ForContext<ConfluenceClient>();
            _configuration = configuration;
            _secretsProvider = secretsProvider;

            // Bind the color mappings from configuration
            _colorMappings = new ConfluenceColorMappings();
            _configuration.GetSection("ConfluenceColorMappings").Bind(_colorMappings);

        }

        #region GetAllDatabaseItemsAsync
        public async Task<List<ConfluenceRow>> GetAllDatabaseItemsAsync(string databaseId, CancellationToken cancellationToken = default)
        {
            _logger.Debug("Stub: Returning mock data for GetAllDatabaseItemsAsync()");
            return new List<ConfluenceRow>
            {
                new ConfluenceRow
                {
                    Id = "row-001",
                    ExternalId = "abc-123",
                    Title = "Stub Entry",
                    LastModifiedUtc = DateTime.UtcNow.AddHours(-1),
                    Fields = new Dictionary<string, object>
                    {
                        { "Status", "In Progress" },
                        { "Owner", "chip.edw@gmail.com" }
                    }
                }
            };
        }
        #endregion

        //#region CreateDatabaseItemAsync
        //public async Task<bool> CreateDatabaseItemAsync(ConfluenceRow row, string databaseId)
        //{
        //    _logger.Information("Creating new item in Confluence database {DatabaseId} with title '{Title}'", databaseId, row.Title);

        //    // TODO: Replace with real API call later
        //    await Task.Delay(100); // simulate latency

        //    _logger.Information("Successfully mocked creation of database item with ExternalId {ExternalId}", row.ExternalId);
        //    return true;
        //}
        //#endregion

        #region GetDatabaseEntriesAsync
        public async Task<List<ConfluenceDatabaseEntryDto>> GetDatabaseEntriesAsync(string databaseId, CancellationToken cancellationToken = default)
        {

            // Get credentials from configuration
            var username = await _secretsProvider.GetApiKeyAsync("ConfluenceUserName");
            var apiToken = await _secretsProvider.GetApiKeyAsync("ConfluenceApiToken");

            var cloudId = _configuration["Confluence:CloudId"];

            var databaseUrl = $"https://api.atlassian.com/ex/confluence/{cloudId}/rest/api/databases/{databaseId}?include-direct-children=true";


            var request = new HttpRequestMessage(HttpMethod.Get, databaseUrl);

            // Use Basic Auth with API token
            var authValue = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{username}:{apiToken}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);

            var response = await _httpClient.SendAsync(request, cancellationToken);


            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);

                return new List<ConfluenceDatabaseEntryDto>();
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.Error($"API Token Error: {errorContent}");
                throw new HttpRequestException($"Failed with API token: {response.StatusCode} - {errorContent}");
            }
        }
        #endregion

        #region GetCustomerPagesAsync
        public async Task<List<ConfluencePage>> GetCustomerPagesAsync(CancellationToken cancellationToken = default)
        {
            var customersParentPageId = _configuration["Confluence:CustomersParentPageId"];

            if (string.IsNullOrEmpty(customersParentPageId))
            {
                throw new InvalidOperationException("CustomersParentPageId not configured in appsettings.json");
            }

            _logger.Debug("Getting customer pages under parent page {ParentPageId}", customersParentPageId);

            var url = $"{_configuration["Confluence:BaseUrl"]}/pages/{customersParentPageId}/children?type=page";

            var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Use API Token auth
            var username = await _secretsProvider.GetApiKeyAsync("ConfluenceUserName");
            var apiToken = await _secretsProvider.GetApiKeyAsync("ConfluenceApiToken");

            var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{apiToken}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            // Parse the JSON response
            var json = JObject.Parse(content);
            var results = json["results"]?.AsArray();
            var customerPages = new List<ConfluencePage>();

            if (results != null)
            {
                foreach (var page in results)
                {
                    var confluencePage = new ConfluencePage
                    {
                        Id = page["id"]?.ToString() ?? "",
                        Title = page["title"]?.ToString() ?? "",
                        Status = page["status"]?.ToString() ?? "",
                        WebUrl = page["_links"]?["webui"]?.ToString() ?? "",
                        Version = page["version"]?["number"]?.Value<int>() ?? 1
                    };

                    // Parse timestamps
                    if (DateTime.TryParse(page["createdAt"]?.ToString(), out var createdAt))
                        confluencePage.CreatedAt = createdAt;
                    if (DateTime.TryParse(page["version"]?["createdAt"]?.ToString(), out var updatedAt))
                        confluencePage.UpdatedAt = updatedAt;

                    // Extract customer name from title (you can adjust this logic as needed)
                    confluencePage.CustomerName = ExtractCustomerNameFromTitle(confluencePage.Title);

                    customerPages.Add(confluencePage);
                }
            }

            _logger.Debug("Successfully retrieved {Count} customer pages", customerPages.Count);
            return customerPages;

            _logger.Debug("Successfully retrieved customer pages");
            return new List<ConfluencePage>(); // Placeholder for now
        }
        #endregion

        #region CreateCustomerPageFromTemplateAsync
        public async Task<string> CreateCustomerPageFromTemplateAsync(string customerName, CancellationToken cancellationToken = default)
        {
            var templatePageId = _configuration["Confluence:CustomerWikiTemplateId"];
            if (string.IsNullOrEmpty(templatePageId))
            {
                throw new InvalidOperationException("CustomerWikiTemplateId not configured in appsettings.json");
            }

            _logger.Information("Creating customer page for {CustomerName} using template {TemplatePageId}", customerName, templatePageId);

            // 1. Get the template page content
            var templatePage = await GetPageWithContentAsync(templatePageId, cancellationToken);
            if (templatePage == null)
            {
                throw new InvalidOperationException($"Template page with ID '{templatePageId}' not found in Confluence");
            }

            // 2. Duplicate the template
            var newPageId = await DuplicatePageAsync(templatePage, customerName, cancellationToken);

            return newPageId;
        }
        #endregion      

        #region GetPageWithContentAsync
        public async Task<ConfluencePage> GetPageWithContentAsync(string pageId, CancellationToken cancellationToken = default)
        {
            _logger.Debug("Getting full content for page {PageId}", pageId);
            // Use v1 API which has better content expansion support
            var baseUrl = _configuration["Confluence:BaseUrl"].Replace("/api/v2", "");
            var url = $"{baseUrl}/rest/api/content/{pageId}?expand=body.atlas_doc_format,body.storage,version";
            _logger.Debug($"DEBUG: Full URL: {url}");
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Use API Token auth
            var username = await _secretsProvider.GetApiKeyAsync("ConfluenceUserName");
            var apiToken = await _secretsProvider.GetApiKeyAsync("ConfluenceApiToken");
            var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{apiToken}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);
            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            // DEBUG: Show raw response
            _logger.Debug($"DEBUG: Raw response length: {content.Length}");
            _logger.Debug($"DEBUG: Raw response sample: {content.Substring(0, Math.Min(500, content.Length))}");
            var json = JObject.Parse(content);
            var page = new ConfluencePage
            {
                Id = json["id"]?.ToString() ?? "",
                Title = json["title"]?.ToString() ?? "",
                Status = json["status"]?.ToString() ?? "",
                Version = json["version"]?["number"]?.Value<int>() ?? 1,
                HtmlContent = json["body"]?["storage"]?["value"]?.ToString(),
                AdfContent = json["body"]?["atlas_doc_format"]?["value"]?.ToString()
            };
            _logger.Debug($"DEBUG: HtmlContent length: {page.HtmlContent?.Length ?? 0}");
            if (!string.IsNullOrEmpty(page.HtmlContent))
            {
                _logger.Debug($"DEBUG: HtmlContent sample: {page.HtmlContent.Substring(0, Math.Min(300, page.HtmlContent.Length))}");
            }
            _logger.Debug($"DEBUG: AdfContent length: {page.AdfContent?.Length ?? 0}");
            if (!string.IsNullOrEmpty(page.AdfContent))
            {
                _logger.Debug($"DEBUG: Full AdfContent: {page.AdfContent}");
            }
            // Parse timestamps
            if (DateTime.TryParse(json["created"]?.ToString(), out var createdAt))
                page.CreatedAt = createdAt;
            if (DateTime.TryParse(json["version"]?["when"]?.ToString(), out var updatedAt))
                page.UpdatedAt = updatedAt;
            page.CustomerName = ExtractCustomerNameFromTitle(page.Title);
            // Check if page has a database - check both HTML and ADF
            page.HasDatabase = CheckForDatabase(page.HtmlContent) || CheckForDatabaseInAdf(page.AdfContent);
            _logger.Debug("Retrieved page content, HasDatabase: {HasDatabase}", page.HasDatabase);
            return page;
        }
        #endregion

        #region CreateTransitionTrackerTableAsync
        public async Task<bool> CreateTransitionTrackerTableAsync(string pageId, string customerName, CancellationToken cancellationToken = default)
        {
            _logger.Information("Creating Transition Tracker table on page {PageId} for customer {CustomerName}", pageId, customerName);

            // Get current page content and version
            var currentPage = await GetPageWithContentAsync(pageId, cancellationToken);

            // Create table structure matching SharePoint TransitionTracker list
            var tableAdf = new JObject
            {
                ["type"] = "table",
                ["attrs"] = new JObject
                {
                    ["layout"] = "default",
                    ["width"] = 900.0,
                    ["localId"] = Guid.NewGuid().ToString()
                },
                ["content"] = new JArray
        {
            // Header row
            new JObject
            {
                ["type"] = "tableRow",
                ["content"] = new JArray
                {
                    CreateTableHeader("Field"),
                    CreateTableHeader("Value")
                }
            },
            // Data rows matching SharePoint field names exactly
            CreateTransitionTrackerRow("Region", CreateRegionStatusCell("")),
            CreateTransitionTrackerRow("Status FF", CreateStatusCell("grey", "Select Status")),
            CreateTransitionTrackerRow("Status Cust.", CreateStatusCell("grey", "Select Status")),
            CreateTransitionTrackerRow("Phase", CreateTextCell("")), // Free form text
            CreateTransitionTrackerRow("Support Impact", CreateSupportImpactCell("")),
            CreateTransitionTrackerRow("Support Accepted", CreateSupportAcceptedCell("")),
            CreateTransitionTrackerRow("Go-Live Date (YYYY-MM-DD)", CreateDateCell("")),
            CreateTransitionTrackerRow("Support Go-Live Date (YYYY-MM-DD)", CreateDateCell("")),
            CreateTransitionTrackerRow("Notes", CreateTextAreaCell("")), // Text field
            CreateTransitionTrackerRow("Sync Tracker", CreateSyncTrackerCell("")) // Yes/No field
        }
            };

            // Add table title
            var titleAdf = new JObject
            {
                ["type"] = "heading",
                ["attrs"] = new JObject { ["level"] = 2 },
                ["content"] = new JArray
        {
            new JObject
            {
                ["type"] = "text",
                ["text"] = $"{customerName} - Transition Tracker"
            }
        }
            };

            // Parse existing ADF content or create new
            JObject adfDoc;
            if (!string.IsNullOrEmpty(currentPage.AdfContent))
            {
                adfDoc = JObject.Parse(currentPage.AdfContent);
            }
            else
            {
                adfDoc = new JObject
                {
                    ["type"] = "doc",
                    ["content"] = new JArray(),
                    ["version"] = 1
                };
            }

            // Add the title and table to the content
            var contentArray = (JArray)adfDoc["content"];
            contentArray.Add(titleAdf);
            contentArray.Add(tableAdf);

            // Update the page
            var baseUrl = _configuration["Confluence:BaseUrl"].Replace("/api/v2", "");
            var url = $"{baseUrl}/rest/api/content/{pageId}";

            var updatePayload = new
            {
                version = new { number = currentPage.Version + 1 },
                title = currentPage.Title,
                type = "page",
                body = new
                {
                    atlas_doc_format = new
                    {
                        value = adfDoc.ToString(Formatting.None),
                        representation = "atlas_doc_format"
                    }
                }
            };

            var request = new HttpRequestMessage(HttpMethod.Put, url);

            var username = await _secretsProvider.GetApiKeyAsync("ConfluenceUserName");
            var apiToken = await _secretsProvider.GetApiKeyAsync("ConfluenceApiToken");

            var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{apiToken}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);

            request.Content = new StringContent(JsonConvert.SerializeObject(updatePayload), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.Information("Successfully created Transition Tracker table on page {PageId}", pageId);
                return true;
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.Error("Failed to create Transition Tracker table on page {PageId}: {Error}", pageId, error);
                return false;
            }
        }

        #endregion

        #region ParseTransitionTrackerTableAsync
        public async Task<Dictionary<string, string>> ParseTransitionTrackerTableAsync(string pageId, CancellationToken cancellationToken = default)
        {
            _logger.Debug("Parsing Transition Tracker table from page {PageId}", pageId);

            var page = await GetPageWithContentAsync(pageId, cancellationToken);

            if (string.IsNullOrEmpty(page.AdfContent))
            {
                _logger.Warning("No ADF content found on page {PageId}", pageId);
                return new Dictionary<string, string>();
            }

            var result = new Dictionary<string, string>();

            try
            {
                var adf = JObject.Parse(page.AdfContent);
                var content = adf["content"] as JArray;

                if (content != null)
                {
                    // Find the transition tracker table
                    foreach (var node in content)
                    {
                        if (node["type"]?.ToString() == "table")
                        {
                            var tableContent = node["content"] as JArray;
                            if (tableContent != null)
                            {
                                result = ParseTableRows(tableContent);
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to parse table data from page {PageId}", pageId);
            }

            _logger.Debug("Parsed {Count} fields from Transition Tracker table", result.Count);
            return result;
        }

        private Dictionary<string, string> ParseTableRows(JArray tableContent)
        {
            var result = new Dictionary<string, string>();

            foreach (var row in tableContent)
            {
                if (row["type"]?.ToString() == "tableRow")
                {
                    var cells = row["content"]?.AsArray();
                    if (cells != null && cells.Count() >= 2)
                    {
                        // First cell is the field name, second cell is the value
                        var fieldName = ExtractTextFromCell(cells[0]);
                        var fieldValue = ExtractValueFromCell(cells[1], fieldName);

                        if (!string.IsNullOrEmpty(fieldName) && !string.IsNullOrEmpty(fieldValue))
                        {
                            result[fieldName] = fieldValue;
                        }
                    }
                }
            }

            return result;
        }

        private string ExtractTextFromCell(JToken cell)
        {
            try
            {
                var content = cell["content"]?.AsArray();
                if (content != null)
                {
                    foreach (var paragraph in content)
                    {
                        var paragraphContent = paragraph["content"]?.AsArray();
                        if (paragraphContent != null)
                        {
                            foreach (var textNode in paragraphContent)
                            {
                                if (textNode["type"]?.ToString() == "text")
                                {
                                    return textNode["text"]?.ToString() ?? "";
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to extract text from cell");
            }

            return "";
        }

        // Replace this method in your ConfluenceClient.cs
        private string ExtractValueFromCell(JToken cell, string fieldName)
        {
            try
            {
                var content = cell["content"]?.AsArray();
                if (content != null)
                {
                    foreach (var paragraph in content)
                    {
                        var paragraphContent = paragraph["content"]?.AsArray();
                        if (paragraphContent != null)
                        {
                            foreach (var node in paragraphContent)
                            {
                                // Check for status macro (color-based fields) - this is the actual value
                                if (node["type"]?.ToString() == "status")
                                {
                                    var statusText = node["attrs"]?["text"]?.ToString();
                                    var color = node["attrs"]?["color"]?.ToString();

                                    // Skip legend text (contains = signs or multiple values)
                                    if (!string.IsNullOrEmpty(statusText) &&
                                        !statusText.Contains("=") &&
                                        !statusText.Contains("|"))
                                    {
                                        _logger.Debug("Found status value for {FieldName}: '{StatusText}' (color: {Color})",
                                            fieldName, statusText, color);
                                        return statusText;
                                    }
                                    else
                                    {
                                        _logger.Debug("Skipping legend text for {FieldName}: '{StatusText}'",
                                            fieldName, statusText);
                                    }
                                }
                                // Check for regular text (for Phase, Notes, etc.)
                                else if (node["type"]?.ToString() == "text")
                                {
                                    var textValue = node["text"]?.ToString();

                                    // Skip legend text and placeholder text
                                    if (!string.IsNullOrEmpty(textValue) &&
                                        !textValue.Contains("=") &&
                                        !textValue.Contains("|") &&
                                        !textValue.StartsWith("Format:") &&
                                        !textValue.StartsWith("📅") &&
                                        textValue != "YYYY-MM-DD")
                                    {
                                        _logger.Debug("Found text value for {FieldName}: '{TextValue}'",
                                            fieldName, textValue);
                                        return textValue;
                                    }
                                    else
                                    {
                                        _logger.Debug("Skipping legend/placeholder text for {FieldName}: '{TextValue}'",
                                            fieldName, textValue);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to extract value from cell for field {FieldName}", fieldName);
            }

            _logger.Debug("No valid value found for field {FieldName}", fieldName);
            return "";
        }

        private string MapColorToValue(string? color, string fieldName)
        {
            if (string.IsNullOrEmpty(color)) return "";

            var mappingSection = fieldName switch
            {
                "Region" => "Region",
                "Status FF" => "StatusFF",
                "Status Cust." => "StatusCust",
                "Support Impact" => "SupportImpact",
                "Support Accepted" => "SupportAccepted",
                "Sync Tracker" => "SyncTracker",
                _ => null
            };

            if (mappingSection == null) return "";

            var mapping = _configuration.GetSection($"ConfluenceColorMappings:{mappingSection}")[color];

            // If no mapping found, return a "please correct" message
            if (string.IsNullOrEmpty(mapping))
            {
                _logger.Debug("Invalid color '{Color}' found for field '{FieldName}'. Prompting user to correct.", color, fieldName);
                return "⚠️ Select correct color";
            }

            return mapping;
        }
        #endregion

        #region UpdateStatusTextBasedOnColorAsync
        public async Task<bool> UpdateStatusTextBasedOnColorAsync(string pageId, CancellationToken cancellationToken = default)
        {
            _logger.Debug("Updating status text based on colors for page {PageId}", pageId);

            var page = await GetPageWithContentAsync(pageId, cancellationToken);

            if (string.IsNullOrEmpty(page.AdfContent))
            {
                _logger.Warning("No ADF content found on page {PageId}", pageId);
                return false;
            }

            try
            {
                var adf = JObject.Parse(page.AdfContent);
                var content = adf["content"]?.AsArray();
                bool hasChanges = false;

                // Find and update the transition tracker table
                foreach (var node in content)
                {
                    if (node["type"]?.ToString() == "table")
                    {
                        hasChanges = UpdateTableStatusText(node);
                        break;
                    }
                }

                if (hasChanges)
                {
                    // Update the page with the modified content
                    var baseUrl = _configuration["Confluence:BaseUrl"].Replace("/api/v2", "");
                    var url = $"{baseUrl}/rest/api/content/{pageId}";

                    var updatePayload = new
                    {
                        version = new { number = page.Version + 1 },
                        title = page.Title,
                        type = "page",
                        body = new
                        {
                            atlas_doc_format = new
                            {
                                value = adf.ToString(Formatting.None),
                                representation = "atlas_doc_format"
                            }
                        }
                    };

                    var request = new HttpRequestMessage(HttpMethod.Put, url);

                    var username = await _secretsProvider.GetApiKeyAsync("ConfluenceUserName");
                    var apiToken = await _secretsProvider.GetApiKeyAsync("ConfluenceApiToken");

                    var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{apiToken}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);

                    request.Content = new StringContent(JsonConvert.SerializeObject(updatePayload), Encoding.UTF8, "application/json");

                    var response = await _httpClient.SendAsync(request, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.Information("Successfully updated status text based on colors for page {PageId}", pageId);
                        return true;
                    }
                    else
                    {
                        var error = await response.Content.ReadAsStringAsync();
                        _logger.Error("Failed to update page {PageId}: {Error}", pageId, error);
                        return false;
                    }
                }

                _logger.Debug("No status text updates needed for page {PageId}", pageId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to update status text for page {PageId}", pageId);
                return false;
            }
        }

        private bool UpdateTableStatusText(JToken tableNode)
        {
            bool hasChanges = false;
            var tableContent = tableNode["content"]?.AsArray();

            if (tableContent != null)
            {
                foreach (var row in tableContent)
                {
                    if (row["type"]?.ToString() == "tableRow")
                    {
                        var cells = row["content"]?.AsArray();
                        if (cells != null && cells.Count() >= 2)
                        {
                            // First cell is field name, second is value
                            var fieldName = ExtractTextFromCell(cells[0]);
                            var updated = UpdateCellStatusText(cells[1], fieldName);
                            if (updated) hasChanges = true;
                        }
                    }
                }
            }

            return hasChanges;
        }

        private bool UpdateCellStatusText(JToken cell, string fieldName)
        {
            try
            {
                var content = cell["content"] as JArray;
                if (content != null)
                {
                    foreach (var paragraph in content)
                    {
                        var paragraphContent = paragraph["content"] as JArray;
                        if (paragraphContent != null)
                        {
                            foreach (var node in paragraphContent)
                            {
                                if (node["type"]?.ToString() == "status")
                                {
                                    var currentColor = node["attrs"]?["color"]?.ToString();
                                    var currentText = node["attrs"]?["text"]?.ToString();
                                    var expectedText = MapColorToValue(currentColor, fieldName);

                                    _logger.Debug($"DEBUG: {fieldName} - Current: {currentColor}/{currentText}, Expected: {expectedText}");

                                    // Check if this is an invalid color (returns warning message)
                                    if (expectedText == "⚠️ Please select correct color")
                                    {
                                        _logger.Debug($"DEBUG: Invalid color detected for {fieldName}, resetting to grey");
                                        node["attrs"]["text"] = expectedText;
                                        node["attrs"]["color"] = "grey";
                                        return true;
                                    }
                                    // Normal update for valid colors
                                    else if (!string.IsNullOrEmpty(expectedText) && currentText != expectedText)
                                    {
                                        _logger.Debug($"DEBUG: Updating {fieldName}: {currentText}→{expectedText}");
                                        node["attrs"]["text"] = expectedText;
                                        // Keep the current color since it's valid
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to update status text for field {FieldName}", fieldName);
            }

            return false;
        }

        private string GetCorrectColorForText(string text)
        {
            // Check all mapping sections for this text value
            foreach (var section in new[] { "StatusFF", "StatusCust", "SupportImpact", "SupportAccepted", "SyncTracker", "Region" })
            {
                var mappings = _configuration.GetSection($"ConfluenceColorMappings:{section}").Get<Dictionary<string, string>>();
                if (mappings != null)
                {
                    var colorKey = mappings.FirstOrDefault(kvp => kvp.Value.Equals(text, StringComparison.OrdinalIgnoreCase)).Key;
                    if (!string.IsNullOrEmpty(colorKey))
                        return colorKey;
                }
            }

            // Fallback for any unmapped values
            return "grey";
        }
        #endregion

        #region UpdateTransitionTrackerFromSharePointAsync
        public async Task<bool> UpdateTransitionTrackerFromSharePointAsync(string pageId, Dictionary<string, string> sharePointData, CancellationToken cancellationToken = default)
        {
            _logger.Information("Updating Transition Tracker table from SharePoint data for page {PageId}", pageId);

            var page = await GetPageWithContentAsync(pageId, cancellationToken);

            if (string.IsNullOrEmpty(page.AdfContent))
            {
                _logger.Warning("No ADF content found on page {PageId}", pageId);
                return false;
            }

            try
            {
                var adf = JObject.Parse(page.AdfContent);
                var content = adf["content"]?.AsArray();
                bool hasChanges = false;

                // Find and update the transition tracker table
                foreach (var node in content)
                {
                    if (node["type"]?.ToString() == "table")
                    {
                        hasChanges = UpdateTableFromSharePointData(node, sharePointData);
                        break;
                    }
                }

                if (hasChanges)
                {
                    // Update the page with the modified content
                    var baseUrl = _configuration["Confluence:BaseUrl"].Replace("/api/v2", "");
                    var url = $"{baseUrl}/rest/api/content/{pageId}";

                    var updatePayload = new
                    {
                        version = new { number = page.Version + 1 },
                        title = page.Title,
                        type = "page",
                        body = new
                        {
                            atlas_doc_format = new
                            {
                                value = adf.ToString(Formatting.None),
                                representation = "atlas_doc_format"
                            }
                        }
                    };

                    var request = new HttpRequestMessage(HttpMethod.Put, url);

                    var username = await _secretsProvider.GetApiKeyAsync("ConfluenceUserName");
                    var apiToken = await _secretsProvider.GetApiKeyAsync("ConfluenceApiToken");

                    var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{apiToken}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);

                    request.Content = new StringContent(JsonConvert.SerializeObject(updatePayload), Encoding.UTF8, "application/json");

                    var response = await _httpClient.SendAsync(request, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.Information("Successfully updated Transition Tracker from SharePoint data for page {PageId}", pageId);
                        return true;
                    }
                    else
                    {
                        var error = await response.Content.ReadAsStringAsync();
                        _logger.Error("Failed to update page {PageId}: {Error}", pageId, error);
                        return false;
                    }
                }

                _logger.Information("No updates needed for page {PageId}", pageId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to update Transition Tracker from SharePoint for page {PageId}", pageId);
                return false;
            }
        }

        private bool UpdateTableFromSharePointData(JToken tableNode, Dictionary<string, string> sharePointData)
        {
            bool hasChanges = false;
            var tableContent = tableNode["content"]?.AsArray();

            if (tableContent != null)
            {
                foreach (var row in tableContent)
                {
                    if (row["type"]?.ToString() == "tableRow")
                    {
                        var cells = row["content"]?.AsArray();
                        if (cells != null && cells.Count() >= 2)
                        {
                            // First cell is field name, second is value
                            var fieldName = ExtractTextFromCell(cells[0]);

                            // Check if we have SharePoint data for this field
                            if (sharePointData.ContainsKey(fieldName))
                            {
                                var newValue = sharePointData[fieldName];
                                var updated = UpdateCellValue(cells[1], fieldName, newValue);
                                if (updated) hasChanges = true;
                            }
                        }
                    }
                }
            }

            return hasChanges;
        }

        private bool UpdateCellValue(JToken cell, string fieldName, string newValue)
        {
            try
            {
                var content = cell["content"] as JArray;
                if (content != null)
                {
                    foreach (var paragraph in content)
                    {
                        var paragraphContent = paragraph["content"] as JArray;
                        if (paragraphContent != null)
                        {
                            foreach (var node in paragraphContent)
                            {
                                // Update status macro fields (color-based) - SKIP LEGEND TEXT
                                if (node["type"]?.ToString() == "status")
                                {
                                    var currentText = node["attrs"]?["text"]?.ToString();

                                    // SKIP if this looks like legend text (contains = or |)
                                    if (!string.IsNullOrEmpty(currentText) &&
                                        (currentText.Contains("=") || currentText.Contains("|")))
                                    {
                                        continue; // Skip legend text, don't update it
                                    }

                                    // NEW RULE: If Confluence has placeholder text, always use SharePoint value
                                    if (IsPlaceholderText(currentText) && !string.IsNullOrEmpty(newValue))
                                    {
                                        node["attrs"]["text"] = newValue;
                                        var newColor = GetCorrectColorForText(newValue);
                                        node["attrs"]["color"] = newColor;
                                        _logger.Information("Replaced placeholder text in {FieldName}: '{PlaceholderText}' → '{NewValue}'",
                                            fieldName, currentText, newValue);
                                        return true;
                                    }

                                    // Normal update for non-placeholder values
                                    if (currentText != newValue && !string.IsNullOrEmpty(newValue) && !IsPlaceholderText(currentText))
                                    {
                                        node["attrs"]["text"] = newValue;
                                        var newColor = GetCorrectColorForText(newValue);
                                        node["attrs"]["color"] = newColor;
                                        _logger.Information("Updated {FieldName}: {OldValue} → {NewValue}", fieldName, currentText, newValue);
                                        return true;
                                    }
                                }
                                // Update text fields (like Phase, Notes, Dates)
                                else if (node["type"]?.ToString() == "text")
                                {
                                    var currentText = node["text"]?.ToString();

                                    // SKIP if this looks like legend text (contains = or |)
                                    if (!string.IsNullOrEmpty(currentText) &&
                                        (currentText.Contains("=") || currentText.Contains("|")))
                                    {
                                        continue; // Skip legend text, don't update it
                                    }

                                    // NEW RULE: Handle text field placeholders
                                    if (IsPlaceholderText(currentText) && !string.IsNullOrEmpty(newValue))
                                    {
                                        node["text"] = newValue;
                                        _logger.Information("Replaced placeholder text in {FieldName}: '{PlaceholderText}' → '{NewValue}'",
                                            fieldName, currentText, newValue);
                                        return true;
                                    }

                                    if (currentText != newValue && !string.IsNullOrEmpty(newValue))
                                    {
                                        node["text"] = newValue;
                                        _logger.Information("Updated {FieldName}: {OldValue} → {NewValue}", fieldName, currentText, newValue);
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to update cell value for field {FieldName}", fieldName);
            }

            return false;
        }

        #endregion

        #region DuplicatePageAsync
        private async Task<string> DuplicatePageAsync(ConfluencePage templatePage, string customerName, CancellationToken cancellationToken = default)
        {
            _logger.Information("Duplicating template page {TemplateId} for customer {CustomerName}", templatePage.Id, customerName);

            try
            {
                var parentPageId = _configuration["Confluence:CustomersParentPageId"];
                var spaceKey = _configuration["Confluence:SpaceKey"];

                if (string.IsNullOrEmpty(parentPageId))
                    throw new InvalidOperationException("CustomersParentPageId not configured in appsettings.json");
                if (string.IsNullOrEmpty(spaceKey))
                    throw new InvalidOperationException("SpaceKey not configured in appsettings.json");

                var baseUrl = _configuration["Confluence:BaseUrl"].Replace("/api/v2", "");
                var url = $"{baseUrl}/rest/api/content";

                var newPagePayload = new
                {
                    type = "page",
                    title = customerName,
                    space = new { key = spaceKey },
                    ancestors = new[] { new { id = parentPageId } },
                    body = new
                    {
                        storage = new
                        {
                            value = templatePage.HtmlContent ?? "",
                            representation = "storage"
                        }
                    }
                };

                var jsonPayload = JsonConvert.SerializeObject(newPagePayload);
                _logger.Debug("Page creation payload: {Payload}", jsonPayload);

                var request = new HttpRequestMessage(HttpMethod.Post, url);

                var username = await _secretsProvider.GetApiKeyAsync("ConfluenceUserName");
                var apiToken = await _secretsProvider.GetApiKeyAsync("ConfluenceApiToken");

                var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{apiToken}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);

                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.Error("Page creation failed with {StatusCode}: {ErrorContent}", response.StatusCode, errorContent);
                    throw new HttpRequestException($"Failed to create page: {response.StatusCode} - {errorContent}");
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var json = JObject.Parse(content);

                var newPageId = json["id"]?.ToString();
                if (string.IsNullOrEmpty(newPageId))
                    throw new InvalidOperationException("Failed to get new page ID from Confluence response");

                _logger.Information("Successfully created new customer page {PageId} for {CustomerName}", newPageId, customerName);
                return newPageId;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to duplicate template page for customer {CustomerName}", customerName);
                throw;
            }
        }
        #endregion

        #region GetPageUrlAsync
        public async Task<string> GetPageUrlAsync(string pageId, CancellationToken cancellationToken = default)
        {
            _logger.Information("Getting page URL for page {PageId}", pageId);

            try
            {
                // Use v1 API to get page info with web URL
                var baseUrl = _configuration["Confluence:BaseUrl"].Replace("/api/v2", "");
                var url = $"{baseUrl}/rest/api/content/{pageId}";

                var request = new HttpRequestMessage(HttpMethod.Get, url);

                // Use API Token auth
                var username = await _secretsProvider.GetApiKeyAsync("ConfluenceUserName");
                var apiToken = await _secretsProvider.GetApiKeyAsync("ConfluenceApiToken");

                var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{apiToken}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var json = JObject.Parse(content);

                // Extract the web UI URL
                var webUrl = json["_links"]?["webui"]?.ToString();

                if (string.IsNullOrEmpty(webUrl))
                {
                    _logger.Warning("No webui URL found for page {PageId}", pageId);
                    // Construct fallback URL
                    var confluenceBaseUrl = _configuration["Confluence:BaseUrl"].Replace("/wiki/api/v2", "").Replace("/api/v2", "");
                    webUrl = $"{confluenceBaseUrl}/wiki/spaces/~{pageId}";
                }
                else
                {
                    // webui URL is usually relative, make it absolute
                    if (webUrl.StartsWith("/"))
                    {
                        var confluenceBaseUrl = _configuration["Confluence:BaseUrl"].Replace("/wiki/api/v2", "").Replace("/api/v2", "");
                        webUrl = $"{confluenceBaseUrl}{webUrl}";
                    }
                }

                _logger.Information("Retrieved page URL for {PageId}: {PageUrl}", pageId, webUrl);
                return webUrl;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get page URL for page {PageId}", pageId);

                // Return a fallback URL if we can't get the real one
                var confluenceBaseUrl = _configuration["Confluence:BaseUrl"].Replace("/wiki/api/v2", "").Replace("/api/v2", "");
                var fallbackUrl = $"{confluenceBaseUrl}/wiki/pages/viewpage.action?pageId={pageId}";

                _logger.Warning("Using fallback URL for page {PageId}: {FallbackUrl}", pageId, fallbackUrl);
                return fallbackUrl;
            }
        }
        #endregion

        #region Helper Method Section

        #region ExtractCustomerNameFromTitle
        private string ExtractCustomerNameFromTitle(string pageTitle)
        {
            // Use the entire page title as the customer name
            return pageTitle.Trim();
        }
        #endregion

        #region CheckForDatabase - Old Delete if not using
        private bool CheckForDatabase(string? htmlContent)
        {
            if (string.IsNullOrEmpty(htmlContent))
                return false;

            // Look for database indicators in the HTML
            return htmlContent.Contains("data-macro-name=\"database\"") ||
                   htmlContent.Contains("ac:name=\"database\"");
        }
        #endregion

        #region CheckForDatabaseInADF
        private bool CheckForDatabaseInAdf(string? adfContent)
        {
            if (string.IsNullOrEmpty(adfContent))
                return false;

            // Look for database indicators in ADF JSON
            return adfContent.Contains("\"type\":\"extension\"") &&
                   (adfContent.Contains("\"extensionType\":\"com.atlassian.confluence.macro.core\"") ||
                    adfContent.Contains("\"extensionKey\":\"database\""));
        }
        #endregion


        #region Helper methods for table creation
        // Helper methods for table creation
        private JObject CreateTableHeader(string text)
        {
            return new JObject
            {
                ["type"] = "tableHeader",
                ["attrs"] = new JObject
                {
                    ["colspan"] = 1,
                    ["rowspan"] = 1
                },
                ["content"] = new JArray
        {
            new JObject
            {
                ["type"] = "paragraph",
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = text,
                        ["marks"] = new JArray
                        {
                            new JObject { ["type"] = "strong" }
                        }
                    }
                }
            }
        }
            };
        }

        private JObject CreateTransitionTrackerRow(string label, JObject valueCell)
        {
            return new JObject
            {
                ["type"] = "tableRow",
                ["content"] = new JArray
        {
            // Label cell (left column)
            new JObject
            {
                ["type"] = "tableHeader",
                ["attrs"] = new JObject
                {
                    ["colspan"] = 1,
                    ["rowspan"] = 1
                },
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "paragraph",
                        ["content"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "text",
                                ["text"] = label,
                                ["marks"] = new JArray
                                {
                                    new JObject { ["type"] = "strong" }
                                }
                            }
                        }
                    }
                }
            },
            // Value cell (right column)
            valueCell
        }
            };
        }

        private JObject CreateStatusCell(string color = "grey", string text = "")
        {
            var legendText = "🔴 = Red | 🟡 = Amber | 🟢 = Green";

            return new JObject
            {
                ["type"] = "tableCell",
                ["attrs"] = new JObject { ["colspan"] = 1, ["rowspan"] = 1 },
                ["content"] = new JArray
        {
            // Legend
            new JObject
            {
                ["type"] = "paragraph",
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = legendText,
                        ["marks"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "textColor",
                                ["attrs"] = new JObject { ["color"] = "#6B7280" }
                            }
                        }
                    }
                }
            },
            // Status
            new JObject
            {
                ["type"] = "paragraph",
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "status",
                        ["attrs"] = new JObject
                        {
                            ["color"] = color,
                            ["text"] = text,
                            ["localId"] = Guid.NewGuid().ToString()
                        }
                    }
                }
            }
        }
            };
        }

        private JObject CreateRegionDropdownCell(string selectedValue)
        {
            if (!string.IsNullOrEmpty(selectedValue))
            {
                var color = GetCorrectColorForText(selectedValue);
                return CreateStatusMacro(color, selectedValue);
            }
            else
            {
                return CreateStatusMacro("grey", "Select Region");
            }
        }

        private JObject CreateStatusMacro(string color, string text)
        {
            return new JObject
            {
                ["type"] = "tableCell",
                ["attrs"] = new JObject
                {
                    ["colspan"] = 1,
                    ["rowspan"] = 1
                },
                ["content"] = new JArray
        {
            new JObject
            {
                ["type"] = "paragraph",
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "status",
                        ["attrs"] = new JObject
                        {
                            ["color"] = color,
                            ["text"] = text,
                            ["localId"] = Guid.NewGuid().ToString()
                        }
                    }
                }
            }
        }
            };
        }

        private JObject CreateTextCell(string text)
        {
            return new JObject
            {
                ["type"] = "tableCell",
                ["attrs"] = new JObject
                {
                    ["colspan"] = 1,
                    ["rowspan"] = 1
                },
                ["content"] = new JArray
        {
            new JObject
            {
                ["type"] = "paragraph",
                ["content"] = string.IsNullOrEmpty(text) ? new JArray() : new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = text
                    }
                }
            }
        }
            };
        }

        private JObject CreateDateCell(string dateValue)
        {
            var displayText = string.IsNullOrEmpty(dateValue)
                ? "YYYY-MM-DD"
                : dateValue;

            return CreateTextCell(displayText);
        }

        private string FormatDateDisplay(string dateValue)
        {
            if (DateTime.TryParse(dateValue, out var date))
            {
                return $"📅 {date:yyyy-MM-dd} ({date:dddd, MMMM d, yyyy})";
            }
            return $"⚠️ Invalid date: {dateValue}";
        }

        private JObject CreateSupportImpactCell(string selectedValue)
        {
            var legendText = "🟢 = Low | 🟡 = Medium | 🔴 = High";
            var color = selectedValue switch
            {
                "Low" => "green",
                "Medium" => "yellow",
                "High" => "red",
                _ => "grey"
            };
            var text = string.IsNullOrEmpty(selectedValue) ? "Select Impact" : selectedValue;

            return new JObject
            {
                ["type"] = "tableCell",
                ["attrs"] = new JObject { ["colspan"] = 1, ["rowspan"] = 1 },
                ["content"] = new JArray
        {
            // Legend
            new JObject
            {
                ["type"] = "paragraph",
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = legendText,
                        ["marks"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "textColor",
                                ["attrs"] = new JObject { ["color"] = "#6B7280" }
                            }
                        }
                    }
                }
            },
            // Status
            new JObject
            {
                ["type"] = "paragraph",
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "status",
                        ["attrs"] = new JObject
                        {
                            ["color"] = color,
                            ["text"] = text,
                            ["localId"] = Guid.NewGuid().ToString()
                        }
                    }
                }
            }
        }
            };
        }

        private JObject CreateSupportAcceptedCell(string selectedValue)
        {
            var legendText = "🟢 = Yes | 🟡 = Pending | 🔴 = No";
            var color = selectedValue switch
            {
                "Yes" => "green",
                "Pending" => "yellow",
                "No" => "red",
                _ => "grey"
            };
            var text = string.IsNullOrEmpty(selectedValue) ? "Select Status" : selectedValue;

            return new JObject
            {
                ["type"] = "tableCell",
                ["attrs"] = new JObject { ["colspan"] = 1, ["rowspan"] = 1 },
                ["content"] = new JArray
        {
            // Legend
            new JObject
            {
                ["type"] = "paragraph",
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = legendText,
                        ["marks"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "textColor",
                                ["attrs"] = new JObject { ["color"] = "#6B7280" }
                            }
                        }
                    }
                }
            },
            // Status
            new JObject
            {
                ["type"] = "paragraph",
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "status",
                        ["attrs"] = new JObject
                        {
                            ["color"] = color,
                            ["text"] = text,
                            ["localId"] = Guid.NewGuid().ToString()
                        }
                    }
                }
            }
        }
            };
        }

        private JObject CreateTextAreaCell(string text)
        {
            // Multi-line text for Notes
            return CreateTextCell(string.IsNullOrEmpty(text) ? "[Enter notes here]" : text);
        }

        private JObject CreateSyncTrackerCell(string selectedValue)
        {
            var legendText = "🟢 = Yes | 🔴 = No";
            var color = selectedValue switch
            {
                "Yes" => "green",
                "No" => "red",
                _ => "grey"
            };
            var text = string.IsNullOrEmpty(selectedValue) ? "Select Yes/No" : selectedValue;

            return new JObject
            {
                ["type"] = "tableCell",
                ["attrs"] = new JObject { ["colspan"] = 1, ["rowspan"] = 1 },
                ["content"] = new JArray
        {
            // Legend
            new JObject
            {
                ["type"] = "paragraph",
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = legendText,
                        ["marks"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "textColor",
                                ["attrs"] = new JObject { ["color"] = "#6B7280" }
                            }
                        }
                    }
                }
            },
            // Status
            new JObject
            {
                ["type"] = "paragraph",
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "status",
                        ["attrs"] = new JObject
                        {
                            ["color"] = color,
                            ["text"] = text,
                            ["localId"] = Guid.NewGuid().ToString()
                        }
                    }
                }
            }
        }
            };
        }

        private JObject CreateRegionStatusCell(string selectedValue)
        {
            var regionLabel = GetRegionLabelFromConfig(selectedValue);
            var regionColor = GetRegionColorFromConfig(selectedValue);
            var legendText = CreateRegionLegend();

            return new JObject
            {
                ["type"] = "tableCell",
                ["attrs"] = new JObject
                {
                    ["colspan"] = 1,
                    ["rowspan"] = 1
                },
                ["content"] = new JArray
                {
                    // Legend text from config
                    new JObject
                    {
                        ["type"] = "paragraph",
                        ["content"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "text",
                                ["text"] = legendText,
                                ["marks"] = new JArray
                                {
                                    new JObject
                                    {
                                        ["type"] = "textColor",
                                        ["attrs"] = new JObject { ["color"] = "#6B7280" }
                                    }
                                }
                            }
                        }
                    },
                    // Status indicator with dropdown capability
                    new JObject
                    {
                        ["type"] = "paragraph",
                        ["content"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "status",
                                ["attrs"] = new JObject
                                {
                                    ["text"] = regionLabel,
                                    ["color"] = regionColor,
                                    ["localId"] = Guid.NewGuid().ToString(),
                                    ["style"] = "",
                                    ["data-region-selector"] = "true",
                                    ["data-region-value"] = selectedValue ?? ""
                                }
                            }
                        }
                    }
                }
            };
        }

        #region Configuration-Based Region Methods

        private string GetRegionColorFromConfig(string regionValue)
        {
            if (string.IsNullOrEmpty(regionValue))
                return "neutral";

            // Find the color key that maps to this region value
            var colorKey = _colorMappings.Region.FirstOrDefault(kvp =>
                kvp.Value.Equals(regionValue, StringComparison.OrdinalIgnoreCase)).Key;

            return colorKey ?? "neutral";
        }

        private string GetRegionLabelFromConfig(string regionValue)
        {
            if (string.IsNullOrEmpty(regionValue))
                return "Select Region";

            var colorKey = GetRegionColorFromConfig(regionValue);
            var emoji = GetColorEmoji(colorKey);

            return $"{emoji} {regionValue}";
        }

        private string CreateRegionLegend()
        {
            var legendParts = new List<string>();

            foreach (var mapping in _colorMappings.Region)
            {
                var emoji = GetColorEmoji(mapping.Key);
                legendParts.Add($"{emoji} = {mapping.Value}");
            }

            return string.Join(" | ", legendParts);
        }

        private string GetColorEmoji(string color)
        {
            return color?.ToLowerInvariant() switch
            {
                "green" => "🟢",
                "yellow" => "🟡",
                "purple" => "🟣",
                "red" => "🔴",
                "blue" => "🔵",
                "grey" or "gray" => "⚪",
                _ => "⚫"
            };
        }

        // Helper method to get all region options for JavaScript (dropdown functionality later)
        private string GetRegionOptionsJson()
        {
            var options = new List<object>
            {
                new { value = "", text = "Select Region", color = "neutral" }
            };

            foreach (var mapping in _colorMappings.Region)
            {
                var emoji = GetColorEmoji(mapping.Key);
                options.Add(new
                {
                    value = mapping.Value,
                    text = $"{emoji} {mapping.Value}",
                    color = mapping.Key
                });
            }

            return JsonConvert.SerializeObject(options);
        }

        #endregion

        // Helper method to detect placeholder text
        private bool IsPlaceholderText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            var lowerText = text.ToLowerInvariant();

            return lowerText.Contains("select correct color") ||
                   lowerText.Contains("⚠️") ||
                   text == "YYYY-MM-DD" ||
                   text == "[Enter notes here]" ||
                   lowerText.Contains("select") && lowerText.Contains("color") ||
                   lowerText.Contains("select status") ||
                   lowerText.Contains("select impact") ||
                   lowerText.Contains("select region") ||
                   lowerText.Contains("select yes/no") ||
                   lowerText.Contains("please select") ||
                   text.Trim() == ""; // Empty text is also placeholder
        }



        #endregion


        #endregion

    }
}
