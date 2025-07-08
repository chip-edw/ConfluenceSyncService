using ConfluenceSyncService.Common;
using ConfluenceSyncService.Common.Secrets;
using ConfluenceSyncService.Dtos;
using ConfluenceSyncService.Models;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ConfluenceSyncService.Auth
{
    public interface IConfluenceAuthClient
    {
        Task<(string AccessToken, string CloudId)> GetAccessTokenAndCloudIdAsync(string profileKey = "Default", CancellationToken cancellationToken = default);
    }

    public class ConfluenceAuthClient : IConfluenceAuthClient
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ISecretsProvider _secretsProvider;
        private readonly Microsoft.Extensions.Logging.ILogger<ConfluenceAuthClient> _logger;

        private readonly ConcurrentDictionary<string, ConfluenceTokenInfo> _tokenCache = new();


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

        public async Task<(string AccessToken, string CloudId)> GetAccessTokenAndCloudIdAsync(string profileKey = "Default", CancellationToken cancellationToken = default)
        {
            if (_tokenCache.TryGetValue(profileKey, out var cached) && !cached.IsExpired())
            {
                return (cached.AccessToken, cached.CloudId);
            }


            _logger.LogInformation("Refreshing Confluence access token for profile '{Profile}'...", profileKey);

            var tokenResult = await AcquireAccessTokenAsync(profileKey, cancellationToken);
            var cloudId = await GetCloudIdAsync(tokenResult.AccessToken, cancellationToken);

            var tokenInfo = new ConfluenceTokenInfo
            {
                AccessToken = tokenResult.AccessToken,
                RefreshToken = tokenResult.RefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResult.ExpiresIn),
                CloudId = cloudId
            };

            _tokenCache.AddOrUpdate(profileKey, tokenInfo, (_, _) => tokenInfo);

            // Save new refresh token
            await _secretsProvider.SaveRefreshTokenAsync($"ConfluenceOAuth:Profiles:{profileKey}:RefreshToken", tokenResult.RefreshToken);

            return (tokenInfo.AccessToken, tokenInfo.CloudId);
        }

        private async Task<(string AccessToken, string RefreshToken, int ExpiresIn)> AcquireAccessTokenAsync(string profileKey, CancellationToken cancellationToken)
        {
            var clientIdKey = ConfluenceOAuthKeys.GetClientIdKey(profileKey);
            var clientSecretKey = ConfluenceOAuthKeys.GetClientSecretKey(profileKey);
            var refreshTokenKey = ConfluenceOAuthKeys.GetRefreshTokenKey(profileKey);

            var clientId = await _secretsProvider.GetApiKeyAsync(clientIdKey);
            var clientSecret = await _secretsProvider.GetApiKeyAsync(clientSecretKey);
            var refreshToken = await _secretsProvider.GetApiKeyAsync(refreshTokenKey);

            _logger.LogInformation("Acquiring token using profile '{Profile}' and refresh token key '{Key}'", profileKey, refreshTokenKey);

            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                _logger.LogCritical("No refresh token found under key '{Key}'", refreshTokenKey);
                throw new InvalidOperationException("No refresh token found.");
            }

            var client = _httpClientFactory.CreateClient();
            var requestBody = new
            {
                grant_type = "refresh_token",
                client_id = clientId,
                client_secret = clientSecret,
                refresh_token = refreshToken
            };

            var response = await client.PostAsJsonAsync("https://auth.atlassian.com/oauth/token", requestBody, cancellationToken);
            response.EnsureSuccessStatusCode();

            var tokenResponse = await response.Content.ReadFromJsonAsync<ConfluenceTokenResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Failed to deserialize token response from Atlassian.");

            return (
                tokenResponse.access_token,
                tokenResponse.refresh_token,
                tokenResponse.expires_in
            );
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
