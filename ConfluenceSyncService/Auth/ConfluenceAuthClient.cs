using ConfluenceSyncService.Common.Secrets;
using ConfluenceSyncService.Dtos;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ConfluenceSyncService.Auth
{
    public interface IConfluenceAuthClient
    {
        Task<(string Username, string ApiToken, string CloudId)> GetAuthInfoAsync();

    }

    public class ConfluenceAuthClient : IConfluenceAuthClient
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ISecretsProvider _secretsProvider;
        private readonly Microsoft.Extensions.Logging.ILogger<ConfluenceAuthClient> _logger;



        public ConfluenceAuthClient(
            IConfiguration config,
            IHttpClientFactory httpClientFactory,
            ISecretsProvider secretsProvider,
            ILogger<ConfluenceAuthClient> logger)
        {
            _config = config;
            _httpClientFactory = httpClientFactory;
            _secretsProvider = secretsProvider;
            _logger = logger;
        }

        public async Task<(string Username, string ApiToken, string CloudId)> GetAuthInfoAsync()
        {
            var username = _config["Confluence:Username"];
            var apiToken = _config["Confluence:ApiToken"];
            var cloudId = _config["Confluence:CloudId"];

            if (string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(apiToken) ||
                string.IsNullOrWhiteSpace(cloudId))
            {
                throw new InvalidOperationException("Missing one or more required Confluence credentials.");
            }

            return (username, apiToken, cloudId);
        }


        private async Task<string> GetCloudIdAsync(string accessToken, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await client.GetAsync("https://api.atlassian.com/oauth/token/accessible-resources", cancellationToken);
            response.EnsureSuccessStatusCode();

            var resources = await response.Content.ReadFromJsonAsync<AtlassianAccessibleResource[]>(cancellationToken)
                ?? throw new InvalidOperationException("Unable to parse accessible-resources response.");

            _logger.LogDebug("Found {Count} accessible resources", resources.Length);
            foreach (var resource in resources)
            {
                _logger.LogDebug("Resource: ID={Id}, Product={Product}, Name={Name}, URL={Url}",
                    resource.id, resource.product ?? "null", resource.name, resource.url);
            }

            if (resources.Length == 0)
            {
                throw new InvalidOperationException("No accessible resources found. This usually indicates insufficient OAuth scopes or the app doesn't have access to any Atlassian products.");
            }

            // Look for Confluence resources by URL pattern (ends with .atlassian.net) 
            // and has confluence-related scopes
            var confluenceResource = resources.FirstOrDefault(r =>
                r.url.EndsWith(".atlassian.net", StringComparison.OrdinalIgnoreCase) &&
                r.scopes.Any(scope => scope.Contains("confluence", StringComparison.OrdinalIgnoreCase)));

            if (confluenceResource == null)
            {
                // Fallback: if only one resource and it has confluence scopes, assume it's Confluence
                if (resources.Length == 1 &&
                    resources[0].scopes.Any(scope => scope.Contains("confluence", StringComparison.OrdinalIgnoreCase)))
                {
                    confluenceResource = resources[0];
                }
            }

            if (confluenceResource == null)
            {
                var resourceDetails = string.Join(", ", resources.Select(r =>
                    $"ID: {r.id}, Name: {r.name}, URL: {r.url}, Scopes: [{string.Join(", ", r.scopes)}]"));
                throw new InvalidOperationException($"Confluence resource not found. Available resources: {resourceDetails}");
            }

            _logger.LogInformation("Selected Confluence resource: ID={Id}, Name={Name}, URL={Url}",
                confluenceResource.id, confluenceResource.name, confluenceResource.url);

            return confluenceResource.id;
        }

    }
}
