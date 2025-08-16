using ConfluenceSyncService.Common.Secrets;
using ConfluenceSyncService.Models.Configuration;
using Serilog;
using System.Runtime.InteropServices;

namespace ConfluenceSyncService
{
    public static class StartupConfiguration
    {
        #region "Dictionaries and Lists"
        public static Dictionary<string, string> protectedSettings = new();
        public static Dictionary<string, string> graphConfigs = new();
        public static Dictionary<string, string> siteIdCache = new();
        public static Dictionary<string, string> listIdCache = new();
        public static List<SharePointSiteConfig> SharePointSites { get; private set; } = new();
        public static TeamsConfig? TeamsConfiguration { get; set; }
        public static EmailConfig? EmailConfiguration { get; set; }
        private static readonly Serilog.ILogger _logger = Log.ForContext(typeof(StartupConfiguration));
        #endregion

        #region "Determine OS"
        public static string DetermineOS()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
            Console.WriteLine("Operating system not recognized");
            return "Unknown";
        }
        #endregion

        #region "Startup and Configuration Methods"
        public static string GetProtectedSetting(string key)
        {
            return protectedSettings[key];
        }

        public static void SetConfig(string key, string value)
        {
            protectedSettings.Add(key, value);
        }

        public static async Task<bool> LoadProtectedSettingsAsync(ISecretsProvider secretsProvider)
        {
            _logger.Debug("Loading Protected Settings via ISecretsProvider");
            protectedSettings.Clear();

            try
            {
                var allKeys = await secretsProvider.GetAllApiKeysAsync();

                foreach (var kvp in allKeys)
                {
                    protectedSettings[kvp.Key] = kvp.Value;
                }

                _logger.Debug($"Protected Settings loaded. Count: {protectedSettings.Count}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to load protected settings via ISecretsProvider");
                return false;
            }
        }

        public static void LoadMsGraphConfig()
        {
            string[] items = { "ClientID", "ClientSecret", "Tenant" };
            foreach (var item in items)
            {
                try
                {
                    graphConfigs[item] = protectedSettings[item];
                }
                catch (Exception ex)
                {
                    _logger.Error($"StartupConfiguration Error loading graphConfigs dictionary: {ex}");
                }
            }

            _logger.Debug("MS Graph Configs Loaded");
        }

        public static void LoadSharePointConfiguration(IConfiguration configuration)
        {
            try
            {
                var config = configuration.GetSection("SharePoint").Get<SharePointSettings>();

                if (config?.Sites != null && config.Sites.Any())
                {
                    SharePointSites = config.Sites;

                    _logger.Information("Loaded {Count} SharePoint site(s) from configuration.", SharePointSites.Count);

                    foreach (var site in SharePointSites)
                    {
                        _logger.Debug("Site loaded: {DisplayName}, Path: {SitePath}", site.DisplayName, site.SitePath);
                        foreach (var list in site.Lists)
                        {
                            _logger.Debug(" List: {DisplayName}, ConfluenceDB ID: {ConfluenceDatabaseId}", list.DisplayName, list.ConfluenceDatabaseId);
                        }
                    }
                }
                else
                {
                    _logger.Warning(" No SharePoint sites found in configuration.");
                }
            }
            catch (Exception ex)
            {
                _logger.Fatal(ex, " Failed to load SharePoint configuration from appsettings.json.");
                Environment.FailFast("Unable to load SharePoint configuration.");
            }
        }

        public static void LoadTeamsConfiguration(IConfiguration configuration)
        {
            var teamsSection = configuration.GetSection("Teams");

            if (!teamsSection.Exists())
            {
                _logger.Warning("No Teams configuration section found in appsettings.json.");
                TeamsConfiguration = null;
                return;
            }

            var teamsConfig = teamsSection.Get<TeamsConfig>();

            if (teamsConfig == null || string.IsNullOrEmpty(teamsConfig.TeamId) || string.IsNullOrEmpty(teamsConfig.ChannelId))
            {
                _logger.Warning("Teams configuration is incomplete - missing TeamId or ChannelId.");
                TeamsConfiguration = null;
                return;
            }

            TeamsConfiguration = teamsConfig;
            _logger.Information("Loaded Teams configuration: {TeamName} -> {ChannelName} (TeamId: {TeamId})",
                teamsConfig.Team ?? "Unknown",
                teamsConfig.Channel ?? "Unknown",
                teamsConfig.TeamId);
        }

        public static void LoadEmailConfiguration(IConfiguration configuration)
        {
            var emailSection = configuration.GetSection("Email");

            if (!emailSection.Exists())
            {
                _logger.Warning("No Email configuration section found in appsettings.json.");
                EmailConfiguration = null;
                return;
            }

            var emailConfig = emailSection.Get<EmailConfig>();

            if (emailConfig == null || string.IsNullOrEmpty(emailConfig.FromEmail))
            {
                _logger.Warning("Email configuration is incomplete - missing FromEmail.");
                EmailConfiguration = null;
                return;
            }

            EmailConfiguration = emailConfig;
            _logger.Information("Loaded Email configuration: From {FromEmail} ({DisplayName})",
                emailConfig.FromEmail,
                emailConfig.FromDisplayName ?? "No Display Name");
        }

        internal static string GetMsGraphConfig(string key)
        {
            return graphConfigs[key];
        }

        public static string GetConfig(string key)
        {
            if (protectedSettings.TryGetValue(key, out string value))
            {
                return value;
            }

            string message = $"[StartupConfiguration] MISSING CONFIG KEY: '{key}' — this is required and the app will now shut down.";
            _logger.Fatal(message);
            Environment.FailFast(message);

            return null!; // unreachable, but required for compilation
        }
        #endregion
    }
}