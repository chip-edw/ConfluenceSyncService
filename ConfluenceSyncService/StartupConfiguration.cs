using ConfluenceSyncService.Models;
using Microsoft.Data.Sqlite;
using Serilog;
using System.Runtime.InteropServices;


namespace ConfluenceSyncService
{
    public static class StartupConfiguration
    {

        #region "Dictionaries and Lists"
        //Dictionary holds all protected app configs loaded from SQLite DB or in future azure vault.
        public static Dictionary<string, string> protectedSettings = new Dictionary<string, string>();

        //Dictionary holds config values specific to MS Graph API - This dictionary loaded from AppSettings
        public static Dictionary<string, string> graphConfigs = new Dictionary<string, string>();

        public static Dictionary<string, string> siteIdCache = new();
        public static Dictionary<string, string> listIdCache = new();
        public static List<SharePointSiteConfig> configuredSites = new();  // SharePoint structure from appsettings.json
        public static List<SharePointSiteConfig> SharePointSites { get; private set; } = new();


        #endregion

        #region " Determine OS "
        /// <summary>
        /// Determine which OS we are operating in. Needed since this is a cross platform App
        /// </summary>
        /// <returns>A string containing one of "Windows", "Linux", "macOS", or "Unknown" </returns>
        public static string DetermineOS()
        {
            string os = string.Empty;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                os = "Windows";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                os = "Linux";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                os = "macOS";
            }
            else
            {
                Console.WriteLine("Operating system not recognized");
                os = "Unknown";
            }

            return os;

        }
        #endregion

        #region "Startup and Configuration Methods"
        public static string GetProtectedSetting(string Tkey)
        {
            return protectedSettings[Tkey];
        }

        public static void SetConfig(string Tkey, string Tvalue)
        {
            protectedSettings.Add(Tkey, Tvalue);
        }

        public static bool LoadProtectedSettings(ApplicationDbContext dbContext)
        {
            Log.Debug("Loading Protected Settings Method Fired from StartupConfiguration");
            Log.Debug("Clearing Dictionary");

            protectedSettings.Clear();

            try
            {
                Log.Debug("Populating dictionary with Protected Settings Key, Value pairs");

                foreach (var setting in dbContext.ConfigStore)
                {
                    protectedSettings[setting.ValueName] = setting.Value;
                }

                Log.Debug($"Protected Settings loaded. Count: {protectedSettings.Count}");
                return true;
            }
            catch (SqliteException ex)
            {
                Log.Error("{0} Unable to read from database: {1}", nameof(LoadProtectedSettings), ex);
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
                    Log.Error($"StartupConfiguration Error loading graphConfigs dictionary  {ex}");
                }

            }
    ;
            Log.Debug("MS Graph Configs Loaded\n");
        }

        public static void LoadSharePointConfig(IConfiguration configuration)
        {
            try
            {
                var config = configuration.GetSection("SharePointConfig").Get<SharePointConfig>();

                if (config?.Sites != null && config.Sites.Any())
                {
                    SharePointSites = config.Sites;
                    Log.Information("Loaded {Count} SharePoint sites from configuration.", SharePointSites.Count);
                }
                else
                {
                    Log.Warning("No SharePoint sites found in configuration.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load SharePoint configuration from appsettings.json.");
            }
        }


        internal static string GetMsGraphConfig(string Tkey)
        {
            return graphConfigs[Tkey];
        }


        public static string GetConfig(string Tkey)
        {
            if (protectedSettings.TryGetValue(Tkey, out string value))
            {
                return value;
            }

            string message = $"[StartupConfiguration] MISSING CONFIG KEY: '{Tkey}' — this is required and the app will now shut down.";
            Log.Fatal(message);

            Environment.FailFast(message);  // Immediately terminates the process the application and any threads such as Schedulers

            return null!;
        }

        public static void LoadSharePointConfiguration(IConfiguration configuration)
        {
            try
            {
                configuredSites = configuration
                    .GetSection("SharePoint:Sites")
                    .Get<List<SharePointSiteConfig>>() ?? new List<SharePointSiteConfig>();

                Log.Information($"Loaded {configuredSites.Count} SharePoint site configurations from appsettings.json");
            }
            catch (Exception ex)
            {
                Log.Fatal("Failed to load SharePoint Sites from configuration. Exception: {Error}", ex);
                Environment.FailFast("Unable to load SharePoint site config from appsettings.json");
            }
        }

        #endregion

    }
}
