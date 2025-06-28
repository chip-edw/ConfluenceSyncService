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
        private readonly IConfidentialClientApplication _app;
        private readonly string[] _scopes;
        private readonly Serilog.ILogger _logger;

        public ConfidentialClientApp(IConfiguration configuration, IMsalHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;

            _logger = Log.ForContext<ConfidentialClientApp>();

            // Get configuration values dynamically
            string apiUrl = _configuration.GetValue<string>("ApiUrl");
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                throw new InvalidOperationException("ApiUrl is not configured.");
            }

            _scopes = new[] { $"{apiUrl}.default" }; // e.g., "https://graph.microsoft.com/.default"

            //string clientId = _configuration.GetValue<string>("AuthenticationConfig:ClientId");
            string clientId = StartupConfiguration.protectedSettings["ClientID"];
            string clientSecret = StartupConfiguration.protectedSettings["ClientSecret"];
            string tenant = StartupConfiguration.GetProtectedSetting("Tenant");
            // Instance is in the Appsettings.json file
            string instance = configuration.GetValue<string>("Instance");
            string authority = String.Format(CultureInfo.InvariantCulture, instance, tenant);

            bool enableMSALLogging = _configuration.GetValue<bool>("LoggingSettings:EnableMSALLogging");

            // Get the desired MSAL logging level from configuration
            var msalLogLevel = _configuration.GetValue<string>("LoggingSettings:MSALLogLevel");

            // Map the string value to Microsoft.Identity.Client.LogLevel. This is so Appsettings.Json controlls logging level
            var parsedMsalLogLevel = msalLogLevel?.ToLower() switch
            {
                "verbose" => Microsoft.Identity.Client.LogLevel.Verbose,
                "info" => Microsoft.Identity.Client.LogLevel.Info,
                "warning" => Microsoft.Identity.Client.LogLevel.Warning,
                "error" => Microsoft.Identity.Client.LogLevel.Error,
                _ => Microsoft.Identity.Client.LogLevel.Warning // Default to Warning
            };

            try
            {
                // Build the confidential client application
                _app = ConfidentialClientApplicationBuilder.Create(clientId)
                    .WithClientSecret(clientSecret)
                    .WithAuthority(new Uri(authority))
                    .WithHttpClientFactory(new MsalHttpClientFactory(_configuration)) // Use the custom factory
                    .WithLogging((IdentityLogLevel level, string message, bool pii) =>
                     {
                         if (enableMSALLogging)
                         {
                             Log.Debug($"[MSAL {level}] {message}");
                         }
                     }, parsedMsalLogLevel, enablePiiLogging: true)
                    .Build();
            }
            catch (Exception ex)
            {
#pragma warning disable CA1416 // Validate platform compatibility
                EventLog.WriteEntry("AutotaskTicketManagementWorkerService", "ConfidentialClientApp failed to initialize: " + ex,
                    EventLogEntryType.Error, 999);
#pragma warning restore CA1416 // Validate platform compatibility
                throw new InvalidOperationException("ConfidentialClientApp failed to initialize.", ex);
            }
        }

        public async Task<string> GetAccessToken()
        {
            try
            {
                Log.Debug("Calling MSAL to AcquireTokenForClient...");

                AuthenticationResult result = await _app.AcquireTokenForClient(_scopes).ExecuteAsync();

                Log.Debug("MSAL token acquired successfully.");

                string accessToken = result.AccessToken;

                // Write token to Authenticate class
                Authenticate.SetAccessToken(accessToken);

                // Get metadata and expiration time
                DateTime expirationTime = result.ExpiresOn.DateTime;
                Authenticate.SetTokenExpiration(expirationTime);

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
