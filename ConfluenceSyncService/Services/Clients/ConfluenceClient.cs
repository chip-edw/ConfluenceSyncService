using ConfluenceSyncService.ConfluenceAPI;
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
        private readonly ConfluenceTokenManager _tokenManager;
        private readonly string _cloudId;
        private readonly IConfiguration _configuration;

        public ConfluenceClient(HttpClient httpClient, ConfluenceTokenManager tokenManager, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _tokenManager = tokenManager;
            _logger = Log.ForContext<ConfluenceClient>();
            _cloudId = _tokenManager.CloudId;
            _configuration = configuration;
        }

        #region GetAllDatabaseItemsAsync
        public async Task<List<ConfluenceRow>> GetAllDatabaseItemsAsync(string databaseId, CancellationToken cancellationToken = default)
        {
            _logger.Information("Stub: Returning mock data for GetAllDatabaseItemsAsync()");
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

        #region CreateDatabaseItemAsync
        public async Task<bool> CreateDatabaseItemAsync(ConfluenceRow row, string databaseId)
        {
            _logger.Information("Creating new item in Confluence database {DatabaseId} with title '{Title}'", databaseId, row.Title);

            // TODO: Replace with real API call later
            await Task.Delay(100); // simulate latency

            _logger.Information("Successfully mocked creation of database item with ExternalId {ExternalId}", row.ExternalId);
            return true;
        }
        #endregion

        #region GetDatabaseEntriesAsync
        public async Task<List<ConfluenceDatabaseEntryDto>> GetDatabaseEntriesAsync(string databaseId, CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"\n=== GETTING DATABASE WITH API TOKEN ===");

            // Get credentials from configuration
            var username = _configuration["Confluence:Username"];
            var apiToken = _configuration["Confluence:ApiToken"];
            var cloudId = _configuration["Confluence:CloudId"];

            var databaseUrl = $"https://api.atlassian.com/ex/confluence/{cloudId}/rest/api/databases/{databaseId}?include-direct-children=true";

            Console.WriteLine($"Database URL: {databaseUrl}");
            Console.WriteLine($"Username: {username}");
            Console.WriteLine($"Using API Token authentication");

            var request = new HttpRequestMessage(HttpMethod.Get, databaseUrl);

            // Use Basic Auth with API token
            var authValue = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{username}:{apiToken}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);

            var response = await _httpClient.SendAsync(request, cancellationToken);

            Console.WriteLine($"Response: {response.StatusCode}");

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"SUCCESS with API Token!");
                Console.WriteLine($"Response sample: {content.Substring(0, Math.Min(500, content.Length))}...");

                // TODO: Parse the actual database entries
                return new List<ConfluenceDatabaseEntryDto>();
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"API Token Error: {errorContent}");
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

            _logger.Information("Getting customer pages under parent page {ParentPageId}", customersParentPageId);

            var url = $"{_configuration["Confluence:BaseUrl"]}/pages/{customersParentPageId}/children?type=page";

            var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Use API Token auth
            var username = _configuration["Confluence:Username"];
            var apiToken = _configuration["Confluence:ApiToken"];
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

            _logger.Information("Successfully retrieved {Count} customer pages", customerPages.Count);
            return customerPages;

            _logger.Information("Successfully retrieved customer pages");
            return new List<ConfluencePage>(); // Placeholder for now
        }
        #endregion

        #region GetRecentlyModifiedItemsAsync
        public async Task<List<ConfluenceRow>> GetRecentlyModifiedItemsAsync(string databaseId, DateTime sinceUtc)
        {
            var results = new List<ConfluenceRow>();
            var sinceIso = sinceUtc.ToString("o");

            var url = $"https://api.atlassian.com/ex/confluence/{_cloudId}/wiki/graphql";

            var query = new
            {
                query = @"
                    query GetModifiedRows($databaseId: ID!, $since: DateTime!) {
                        database(id: $databaseId) {
                            rows {
                                nodes {
                                    id
                                    lastModifiedTime
                                    fields {
                                        name
                                        value
                                    }
                                }
                            }
                        }
                    }",
                variables = new
                {
                    databaseId = databaseId,
                    since = sinceIso
                }
            };

            var (accessToken, _) = await _tokenManager.GetAccessTokenAsync();

            var request = new HttpRequestMessage(HttpMethod.Post, url);

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(JsonConvert.SerializeObject(query), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);
            var nodes = json["data"]?["database"]?["rows"]?["nodes"];

            if (nodes != null)
            {
                foreach (var node in nodes)
                {
                    var rowId = node["id"]?.ToString() ?? "";
                    var modifiedStr = node["lastModifiedTime"]?.ToString();
                    var modified = DateTime.TryParse(modifiedStr, out var dt) ? dt : DateTime.UtcNow;

                    var fields = new Dictionary<string, object>();
                    foreach (var field in node["fields"] ?? new JArray())
                    {
                        var name = field["name"]?.ToString();
                        var value = field["value"];
                        if (!string.IsNullOrWhiteSpace(name))
                            fields[name] = value?.ToString();
                    }

                    results.Add(new ConfluenceRow
                    {
                        Id = rowId,
                        ExternalId = fields.TryGetValue("ExternalId", out var extId) ? extId?.ToString() ?? "" : "",
                        Title = fields.TryGetValue("Title", out var title) ? title?.ToString() ?? "" : "",
                        LastModifiedUtc = modified,
                        Fields = fields
                    });
                }
            }

            _logger.Information("Loaded {Count} recently modified Confluence items since {Since}", results.Count, sinceIso);
            return results;
        }
        #endregion

        #region GetPageWithContentAsync
        #region GetPageWithContentAsync
        #region GetPageWithContentAsync
        public async Task<ConfluencePage> GetPageWithContentAsync(string pageId, CancellationToken cancellationToken = default)
        {
            _logger.Information("Getting full content for page {PageId}", pageId);

            // Use v1 API which has better content expansion support
            var baseUrl = _configuration["Confluence:BaseUrl"].Replace("/api/v2", "");
            var url = $"{baseUrl}/rest/api/content/{pageId}?expand=body.storage,version";

            Console.WriteLine($"DEBUG: Full URL: {url}");

            var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Use API Token auth
            var username = _configuration["Confluence:Username"];
            var apiToken = _configuration["Confluence:ApiToken"];
            var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{apiToken}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            // DEBUG: Show raw response
            Console.WriteLine($"DEBUG: Raw response length: {content.Length}");
            Console.WriteLine($"DEBUG: Raw response sample: {content.Substring(0, Math.Min(500, content.Length))}");

            var json = JObject.Parse(content);

            var page = new ConfluencePage
            {
                Id = json["id"]?.ToString() ?? "",
                Title = json["title"]?.ToString() ?? "",
                Status = json["status"]?.ToString() ?? "",
                Version = json["version"]?["number"]?.Value<int>() ?? 1,
                HtmlContent = json["body"]?["storage"]?["value"]?.ToString()
            };

            Console.WriteLine($"DEBUG: HtmlContent length: {page.HtmlContent?.Length ?? 0}");
            if (!string.IsNullOrEmpty(page.HtmlContent))
            {
                Console.WriteLine($"DEBUG: HtmlContent sample: {page.HtmlContent.Substring(0, Math.Min(300, page.HtmlContent.Length))}");
            }

            // Parse timestamps
            if (DateTime.TryParse(json["created"]?.ToString(), out var createdAt))
                page.CreatedAt = createdAt;
            if (DateTime.TryParse(json["version"]?["when"]?.ToString(), out var updatedAt))
                page.UpdatedAt = updatedAt;

            page.CustomerName = ExtractCustomerNameFromTitle(page.Title);

            // Check if page has a database
            page.HasDatabase = CheckForDatabase(page.HtmlContent);

            _logger.Information("Retrieved page content, HasDatabase: {HasDatabase}", page.HasDatabase);
            return page;
        }
        #endregion
        #endregion
        #endregion











        #region Helper Method Section

        #region ExtractCustomerNameFromTitle
        private string ExtractCustomerNameFromTitle(string pageTitle)
        {
            // Simple extraction - adjust based on your naming convention
            // e.g., "Acme Corp - Transition Tracker" -> "Acme Corp"
            var parts = pageTitle.Split(" - ");
            return parts.Length > 0 ? parts[0].Trim() : pageTitle;
        }
        #endregion

        #region CheckForDatabase
        private bool CheckForDatabase(string? htmlContent)
        {
            if (string.IsNullOrEmpty(htmlContent))
                return false;

            // Look for database indicators in the HTML
            return htmlContent.Contains("data-macro-name=\"database\"") ||
                   htmlContent.Contains("ac:name=\"database\"");
        }
        #endregion

        #endregion

    }
}
