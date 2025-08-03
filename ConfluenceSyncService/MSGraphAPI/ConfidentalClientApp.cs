using Microsoft.Identity.Client;
using Serilog;
using System.Diagnostics;
using System.Globalization;
using IdentityLogLevel = Microsoft.Identity.Client.LogLevel;

namespace ConfluenceSyncService.MSGraphAPI
{
    public class ConfidentialClientApp
    {
        private readonly IConfiguration _configuration;
        private readonly IMsalHttpClientFactory _httpClientFactory;
        private IConfidentialClientApplication _app;
        private string[] _scopes;
        private readonly Serilog.ILogger _logger;
        private bool _isInitialized = false;

        public ConfidentialClientApp(IConfiguration configuration, IMsalHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _logger = Log.ForContext<ConfidentialClientApp>();
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            _logger.Debug("Initializing ConfidentialClientApp...");

            string apiUrl = _configuration.GetValue<string>("ApiUrl");
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                throw new InvalidOperationException("ApiUrl is not configured.");
            }

            _scopes = new[] { $"{apiUrl}.default" }; // e.g., "https://graph.microsoft.com/.default"

            string clientId = StartupConfiguration.GetProtectedSetting("ClientID");
            string clientSecret = StartupConfiguration.GetProtectedSetting("ClientSecret");
            string tenant = StartupConfiguration.GetProtectedSetting("Tenant");
            string instance = _configuration.GetValue<string>("Instance");
            string authority = string.Format(CultureInfo.InvariantCulture, instance, tenant);

            bool enableMSALLogging = _configuration.GetValue<bool>("LoggingSettings:EnableMSALLogging");
            var msalLogLevel = _configuration.GetValue<string>("LoggingSettings:MSALLogLevel")?.ToLower();

            var parsedMsalLogLevel = msalLogLevel switch
            {
                "verbose" => IdentityLogLevel.Verbose,
                "info" => IdentityLogLevel.Info,
                "warning" => IdentityLogLevel.Warning,
                "error" => IdentityLogLevel.Error,
                _ => IdentityLogLevel.Warning
            };

            try
            {
                _app = ConfidentialClientApplicationBuilder.Create(clientId)
                    .WithClientSecret(clientSecret)
                    .WithAuthority(new Uri(authority))
                    .WithHttpClientFactory(_httpClientFactory)
                    .WithLogging((IdentityLogLevel level, string message, bool pii) =>
                    {
                        if (enableMSALLogging)
                        {
                            Log.Debug($"[MSAL {level}] {message}");
                        }
                    }, parsedMsalLogLevel, enablePiiLogging: true)
                    .Build();

                _isInitialized = true;
            }
            catch (Exception ex)
            {
#pragma warning disable CA1416 // Validate platform compatibility
                EventLog.WriteEntry("ConfluenceSyncService", "ConfidentialClientApp failed to initialize: " + ex,
                    EventLogEntryType.Error, 999);
#pragma warning restore CA1416 // Validate platform compatibility
                throw new InvalidOperationException("ConfidentialClientApp failed to initialize.", ex);
            }
        }

        public async Task<string> GetAccessToken()
        {
            await InitializeAsync();

            try
            {
                _logger.Debug("Calling MSAL to AcquireTokenForClient...");

                AuthenticationResult result = await _app.AcquireTokenForClient(_scopes).ExecuteAsync();

                _logger.Debug("MSAL token acquired successfully.");

                string accessToken = result.AccessToken;

                Authenticate.SetAccessToken(accessToken);
                Authenticate.SetTokenExpiration(result.ExpiresOn.DateTime);

                return accessToken;
            }
            catch (MsalException msalEx)
            {
                _logger.Error(msalEx, $"MSAL exception occurred: {msalEx.Message}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Unexpected exception occurred: {ex.Message}");
                throw;
            }
        }

        public async Task DisposeAppAsync()
        {
            if (_app is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (_app is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
