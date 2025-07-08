using ConfluenceSyncService.ConfluenceAPI;
using ConfluenceSyncService.Models.ConfluenceSyncService.Models;
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

        public ConfluenceClient(HttpClient httpClient, ConfluenceTokenManager tokenManager)
        {
            _httpClient = httpClient;
            _tokenManager = tokenManager;
            _logger = Log.ForContext<ConfluenceClient>();
            _cloudId = _tokenManager.CloudId;
        }

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

        public async Task<bool> CreateDatabaseItemAsync(ConfluenceRow row, string databaseId)
        {
            _logger.Information("Creating new item in Confluence database {DatabaseId} with title '{Title}'", databaseId, row.Title);

            // TODO: Replace with real API call later
            await Task.Delay(100); // simulate latency

            _logger.Information("Successfully mocked creation of database item with ExternalId {ExternalId}", row.ExternalId);
            return true;
        }

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
    }
}
