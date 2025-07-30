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
            Log.Debug("Loading Protected Settings via ISecretsProvider");
            protectedSettings.Clear();

            try
            {
                var allKeys = await secretsProvider.GetAllApiKeysAsync();

                foreach (var kvp in allKeys)
                {
                    protectedSettings[kvp.Key] = kvp.Value;
                }

                Log.Debug($"Protected Settings loaded. Count: {protectedSettings.Count}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load protected settings via ISecretsProvider");
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
                    Log.Error($"StartupConfiguration Error loading graphConfigs dictionary: {ex}");
                }
            }

            Log.Debug("MS Graph Configs Loaded");
        }

        public static void LoadSharePointConfiguration(IConfiguration configuration)
        {
            try
            {
                var config = configuration.GetSection("SharePoint").Get<SharePointSettings>();

                if (config?.Sites != null && config.Sites.Any())
                {
                    SharePointSites = config.Sites;

                    Log.Information("Loaded {Count} SharePoint site(s) from configuration.", SharePointSites.Count);

                    foreach (var site in SharePointSites)
                    {
                        Log.Debug("Site loaded: {DisplayName}, Path: {SitePath}", site.DisplayName, site.SitePath);
                        foreach (var list in site.Lists)
                        {
                            Log.Debug(" List: {DisplayName}, ConfluenceDB ID: {ConfluenceDatabaseId}", list.DisplayName, list.ConfluenceDatabaseId);
                        }
                    }
                }
                else
                {
                    Log.Warning(" No SharePoint sites found in configuration.");
                }
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, " Failed to load SharePoint configuration from appsettings.json.");
                Environment.FailFast("Unable to load SharePoint configuration.");
            }
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
            Log.Fatal(message);
            Environment.FailFast(message);

            return null!; // unreachable, but required for compilation
        }
        #endregion
    }
}
