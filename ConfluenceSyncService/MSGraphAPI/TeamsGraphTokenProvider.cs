using ConfluenceSyncService.Common.Secrets;
using System.Text.Json;

namespace ConfluenceSyncService.MSGraphAPI
{
    public interface ITeamsGraphTokenProvider
    {
        Task<string> GetTokenAsync(CancellationToken ct);
    }

    public class TeamsGraphTokenProvider : ITeamsGraphTokenProvider
    {
        private readonly ISecretsProvider _secrets;
        private readonly Serilog.ILogger _logger;
        private readonly HttpClient _httpClient;

        public TeamsGraphTokenProvider(ISecretsProvider secrets, Serilog.ILogger logger, HttpClient httpClient)
        {
            _secrets = secrets;
            _logger = logger;
            _httpClient = httpClient;
        }

        public async Task<string> GetTokenAsync(CancellationToken ct)
        {
            try
            {
                var clientId = await _secrets.GetApiKeyAsync("ClientID");
                var clientSecret = await _secrets.GetApiKeyAsync("ClientSecret");
                var tenantId = await _secrets.GetApiKeyAsync("Tenant");
                var password = await _secrets.GetApiKeyAsync("ServiceAccount-Password");

                if (string.IsNullOrWhiteSpace(clientId))
                    throw new InvalidOperationException("ClientID not found in Key Vault");
                if (string.IsNullOrWhiteSpace(clientSecret))
                    throw new InvalidOperationException("ClientSecret not found in Key Vault");
                if (string.IsNullOrWhiteSpace(tenantId))
                    throw new InvalidOperationException("Tenant not found in Key Vault");
                if (string.IsNullOrWhiteSpace(password))
                    throw new InvalidOperationException("ServiceAccount-Password not found in Key Vault");

                var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

                var requestBody = new List<KeyValuePair<string, string>>
            {
                new("client_id", clientId),
                new("client_secret", clientSecret),
                new("scope", "https://graph.microsoft.com/.default"),
                new("username", "confluence-middleware@v7n2m.onmicrosoft.com"),
                new("password", password),
                new("grant_type", "password")
            };

                var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
                {
                    Content = new FormUrlEncodedContent(requestBody)
                };

                var response = await _httpClient.SendAsync(request, ct);
                var responseContent = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.Error("Token request failed: Status={Status}, Response={Response}",
                        response.StatusCode, responseContent);
                    throw new InvalidOperationException($"Token request failed: {responseContent}");
                }

                var tokenResponse = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(responseContent);
                var accessToken = tokenResponse.GetProperty("access_token").GetString();

                _logger.Debug("Successfully acquired delegated token for Teams operations");
                return accessToken ?? throw new InvalidOperationException("Access token was null");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to acquire delegated token for Teams operations");
                throw;
            }
        }
    }
}

