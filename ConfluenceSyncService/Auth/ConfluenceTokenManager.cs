using ConfluenceSyncService.Auth;

namespace ConfluenceSyncService.ConfluenceAPI
{
    public class ConfluenceTokenManager
    {
        private readonly IConfluenceAuthClient _authClient;
        private readonly ILogger<ConfluenceTokenManager> _logger;
        private string? _accessToken;
        private DateTimeOffset _expiresAt;
        private string? _cloudId;
        private readonly string _defaultProfile;

        public DateTimeOffset ExpiresAt => _expiresAt;


        public ConfluenceTokenManager(IConfiguration config, IConfluenceAuthClient authClient, ILogger<ConfluenceTokenManager> logger)
        {
            _authClient = authClient;
            _logger = logger;
            _defaultProfile = config["ConfluenceOAuth:DefaultProfile"] ?? "Default";
        }

        public string? CloudId => _cloudId;

        public async Task<(string AccessToken, string CloudId)> GetAccessTokenAsync(string? profileKey = null, CancellationToken cancellationToken = default)
        {
            var resolvedProfile = profileKey ?? _defaultProfile;

            if (_accessToken != null && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                _logger.LogDebug("Using cached access token for profile '{Profile}'", resolvedProfile);
                return (_accessToken, _cloudId!);
            }

            _logger.LogInformation("Access token expired or missing. Refreshing for profile '{Profile}'...", resolvedProfile);

            var (accessToken, cloudId) = await _authClient.GetAccessTokenAndCloudIdAsync(resolvedProfile, cancellationToken);

            _accessToken = accessToken;
            _cloudId = cloudId;
            _expiresAt = DateTimeOffset.UtcNow.AddMinutes(55);

            _logger.LogInformation("New token acquired for profile '{Profile}'. Expires at {ExpiresAt:u}", resolvedProfile, _expiresAt);

            return (_accessToken, _cloudId!);
        }

    }
}
