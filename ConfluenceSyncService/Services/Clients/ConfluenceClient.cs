using ConfluenceSyncService.Models;
using Serilog;

namespace ConfluenceSyncService.Services.Clients
{
    public class ConfluenceClient
    {
        private readonly HttpClient _httpClient;
        private readonly Serilog.ILogger _logger;

        public ConfluenceClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _logger = Log.ForContext<ConfluenceClient>();
        }

        public async Task<List<ConfluenceRow>> GetAllDatabaseItemsAsync(string databaseId, CancellationToken cancellationToken = default)
        {
            // Temporary stub to allow build to succeed
            return new List<ConfluenceRow>
    {
        new ConfluenceRow
        {
            ExternalId = "abc-123",
            Title = "Stub Entry",
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

            // TODO: Replace with actual API call when you're ready
            await Task.Delay(100); // simulate latency

            _logger.Information("Successfully mocked creation of database item with ExternalId {ExternalId}", row.ExternalId);
            return true;
        }
    }
}
