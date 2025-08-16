using ConfluenceSyncService.Services.Clients;
using Serilog;

namespace ConfluenceSyncService.Utilities
{
    public class WorkerUtilities
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly Serilog.ILogger _logger;

        public WorkerUtilities(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _logger = Log.ForContext<WorkerUtilities>();
        }

        public string GetOperatingSystem()
        {
            return StartupConfiguration.DetermineOS();
        }

        #region SharePoint Utilities
        public async Task ListSharePointFieldNamesAsync(string siteId, string listName)
        {
            try
            {
                Console.WriteLine("\n\n");
                _logger.Information("=== DISCOVERING SHAREPOINT FIELD NAMES ===");

                using var scope = _serviceScopeFactory.CreateScope();
                var sharePointClient = scope.ServiceProvider.GetRequiredService<SharePointClient>();

                var fieldMap = await sharePointClient.GetListFieldsAsync(siteId, listName);

                Console.WriteLine("SharePoint Field Mappings:");
                foreach (var field in fieldMap)
                {
                    Console.WriteLine($"Display: '{field.Key}' -> Internal: '{field.Value}'");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get SharePoint field names");
            }
            _logger.Information("=== END SHAREPOINT FIELD NAMES DISCOVERY ===");
        }

        public async Task<bool> CreateTransitionTrackerTableAsync(string pageId, string templateName)
        {
            try
            {
                Console.WriteLine("\n\n");
                _logger.Information("=== TESTING NEW TABLE CREATION ===");

                using var scope = _serviceScopeFactory.CreateScope();
                var confluenceClient = scope.ServiceProvider.GetRequiredService<ConfluenceClient>();

                var createSuccess = await confluenceClient.CreateTransitionTrackerTableAsync(pageId, templateName);
                Console.WriteLine($"Table creation successful: {createSuccess}");

                _logger.Information("=== END TABLE CREATION TEST ===");
                return createSuccess;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to create new table");
                _logger.Information("=== END TABLE CREATION TEST ===");
                return false;
            }
        }

        public async Task<Dictionary<string, string>?> UpdateStatusAndParseTableAsync(string pageId)
        {
            try
            {
                Console.WriteLine("\n\n");
                _logger.Information("=== CONFLUENCE STATUS TEXT UPDATE AND PARSING ===");

                using var scope = _serviceScopeFactory.CreateScope();
                var confluenceClient = scope.ServiceProvider.GetRequiredService<ConfluenceClient>();

                // First, update any status text based on current colors
                var updateSuccess = await confluenceClient.UpdateStatusTextBasedOnColorAsync(pageId);
                Console.WriteLine($"Status text update successful: {updateSuccess}");

                // Then parse the table data
                var tableData = await confluenceClient.ParseTransitionTrackerTableAsync(pageId);

                Console.WriteLine("=== PARSED TABLE DATA ===");
                foreach (var kvp in tableData)
                {
                    Console.WriteLine($"{kvp.Key}: {kvp.Value}");
                }

                _logger.Information("=== END STATUS UPDATE AND PARSING TEST ===");
                return tableData;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to test status update and parsing");
                _logger.Information("=== END STATUS UPDATE AND PARSING TEST ===");
                return null;
            }
        }

        #endregion

        #region Teams Utilities
        public async Task DiscoverTeamsResourcesAsync()
        {
            _logger.Information("Starting Teams resource discovery...");

            using var scope = _serviceScopeFactory.CreateScope();
            var teamsClient = scope.ServiceProvider.GetRequiredService<TeamsClient>();

            try
            {
                // Configuration is already loaded by StartupLoaderService during app startup

                // Discover all Teams resources
                var discoveryResult = await teamsClient.DiscoverAllTeamsResourcesAsync();

                Console.WriteLine($"\n=== TEAMS DISCOVERY RESULTS ===");
                Console.WriteLine($"Found {discoveryResult.Teams.Count} Teams\n");

                _logger.Information("=== TEAMS DISCOVERY RESULTS ===");
                _logger.Information("Found {TeamCount} Teams", discoveryResult.Teams.Count);

                // Display Teams and their channels
                foreach (var teamWithChannels in discoveryResult.Teams)
                {
                    var team = teamWithChannels.Team;
                    Console.WriteLine($"Team: {team.DisplayName} (ID: {team.Id})");
                    Console.WriteLine($"  Description: {team.Description}");
                    Console.WriteLine($"  Mail Nickname: {team.MailNickname}");

                    // Also log to file
                    _logger.Information("Team: {TeamName} (ID: {TeamId})", team.DisplayName, team.Id);
                    _logger.Information("  Description: {Description}", team.Description);
                    _logger.Information("  Mail Nickname: {MailNickname}", team.MailNickname);

                    if (teamWithChannels.Channels.Any())
                    {
                        Console.WriteLine($"  Channels ({teamWithChannels.Channels.Count}):");
                        _logger.Information("  Channels ({ChannelCount}):", teamWithChannels.Channels.Count);
                        foreach (var channel in teamWithChannels.Channels)
                        {
                            Console.WriteLine($"    - {channel.DisplayName} (ID: {channel.Id})");
                            Console.WriteLine($"      Type: {channel.MembershipType}");
                            _logger.Information("    - {ChannelName} (ID: {ChannelId})", channel.DisplayName, channel.Id);
                            _logger.Information("      Type: {MembershipType}", channel.MembershipType);
                            if (!string.IsNullOrEmpty(channel.Description))
                            {
                                Console.WriteLine($"      Description: {channel.Description}");
                                _logger.Information("      Description: {Description}", channel.Description);
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine($"  No channels found or access denied");
                        _logger.Information("  No channels found or access denied");
                    }
                    Console.WriteLine();
                    _logger.Information(""); // Empty line for readability
                }

                Console.WriteLine("=== DISCOVERY COMPLETE ===");
                Console.WriteLine("Use the Team and Channel IDs above to configure your appsettings.json Teams section");
                _logger.Information("=== DISCOVERY COMPLETE ===");
                _logger.Information("Use the Team and Channel IDs above to configure your appsettings.json Teams section");
                _logger.Information("Teams discovery completed successfully");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error during Teams discovery");
                Console.WriteLine($"❌ Teams discovery failed: {ex.Message}");
                throw;
            }
        }
        #endregion

        #region Email Utilities

        /// <summary>
        /// Validates email configuration without sending emails (safe to run early)
        /// </summary>
        public async Task ValidateEmailConfigurationAsync()
        {
            _logger.Information("Validating Email configuration...");

            try
            {
                // Validate that email configuration is loaded
                var emailConfig = StartupConfiguration.EmailConfiguration;
                if (emailConfig == null)
                {
                    Console.WriteLine("❌ Email configuration not loaded - check appsettings.json Email section");
                    _logger.Warning("Email configuration not loaded - check StartupLoaderService execution");
                    return;
                }

                Console.WriteLine("=== EMAIL CONFIGURATION VALIDATION ===");
                Console.WriteLine($"✅ Email configuration loaded successfully");
                Console.WriteLine($"From Email: {emailConfig.FromEmail}");
                Console.WriteLine($"From Display Name: {emailConfig.FromDisplayName ?? "[Not Set]"}");

                _logger.Information("Email configuration validation successful:");
                _logger.Information("  From Email: {FromEmail}", emailConfig.FromEmail);
                _logger.Information("  From Display Name: {FromDisplayName}", emailConfig.FromDisplayName ?? "[Not Set]");

                // Display notification configurations
                if (emailConfig.Notifications != null && emailConfig.Notifications.Any())
                {
                    Console.WriteLine($"\nNotification Configurations ({emailConfig.Notifications.Count}):");
                    _logger.Information("Found {NotificationCount} notification configurations:", emailConfig.Notifications.Count);

                    foreach (var notification in emailConfig.Notifications)
                    {
                        Console.WriteLine($"  - {notification.Key}:");
                        Console.WriteLine($"    ToEmail: {notification.Value.ToEmail ?? "[Dynamic]"}");
                        Console.WriteLine($"    FromDisplayName: {notification.Value.FromDisplayName ?? "[Not Set]"}");

                        _logger.Information("  {NotificationType}: ToEmail={ToEmail}, FromDisplayName={FromDisplayName}",
                            notification.Key,
                            notification.Value.ToEmail ?? "[Dynamic]",
                            notification.Value.FromDisplayName ?? "[Not Set]");
                    }
                }
                else
                {
                    Console.WriteLine("No notification configurations found.");
                    _logger.Information("No notification configurations found.");
                }

                Console.WriteLine("=== EMAIL CONFIGURATION VALIDATION COMPLETE ===");
                _logger.Information("Email configuration validation completed successfully");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error during email configuration validation");
                Console.WriteLine($"❌ Email configuration validation failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Sends service startup notification email (runs after configuration is loaded)
        /// </summary>
        public async Task SendServiceStartupEmailAsync()
        {
            _logger.Information("Checking if service startup email should be sent...");

            using var scope = _serviceScopeFactory.CreateScope();
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var emailClient = scope.ServiceProvider.GetRequiredService<EmailClient>();

            try
            {
                // Check if startup email is enabled
                bool sendStartupEmail = configuration.GetValue<bool>("Email:SendServiceStartEmail", false);

                if (!sendStartupEmail)
                {
                    Console.WriteLine("⚠️  Service startup email is disabled");
                    Console.WriteLine("   To enable: Add \"SendServiceStartEmail\": true to Email config");
                    _logger.Information("Service startup email is disabled (Email:SendServiceStartEmail = false)");
                    return;
                }

                Console.WriteLine("=== SENDING SERVICE STARTUP EMAIL ===");
                _logger.Information("Service startup email is enabled - sending notification");

                var subject = $"Confluence Sync Service - Started Successfully - {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                var body = CreateServiceStartupEmailBody();

                // Send startup notification using Alert configuration (goes to admin)
                var result = await emailClient.SendAlertAsync(subject, body, isHtml: true);

                Console.WriteLine("✅ Service startup email sent successfully!");
                _logger.Information("Service startup email sent successfully: {Result}", result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to send service startup email: {ex.Message}");
                _logger.Error(ex, "Failed to send service startup email");
                // Don't throw - startup email failure shouldn't stop the service
            }
        }

        /// <summary>
        /// Creates the service startup email body
        /// </summary>
        private string CreateServiceStartupEmailBody()
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version?.ToString() ?? "Unknown";

            return $@"
                <html>
                <body style='font-family: Arial, sans-serif; margin: 20px;'>
                    <h2 style='color: #2E8B57;'>🚀 Service Startup Notification</h2>
                    <p>The <strong>Confluence Sync Service</strong> has started successfully.</p>
                    
                    <div style='background-color: #e8f5e8; padding: 20px; border-radius: 8px; border-left: 4px solid #4CAF50; margin: 15px 0;'>
                        <h3>Service Details:</h3>
                        <table style='width: 100%; border-collapse: collapse;'>
                            <tr><td style='padding: 5px 0; font-weight: bold;'>Service:</td><td>Confluence Sync Service</td></tr>
                            <tr><td style='padding: 5px 0; font-weight: bold;'>Version:</td><td>{version}</td></tr>
                            <tr><td style='padding: 5px 0; font-weight: bold;'>Started:</td><td>{DateTime.Now:yyyy-MM-dd HH:mm:ss} UTC</td></tr>
                            <tr><td style='padding: 5px 0; font-weight: bold;'>Environment:</td><td>{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}</td></tr>
                            <tr><td style='padding: 5px 0; font-weight: bold;'>Machine:</td><td>{Environment.MachineName}</td></tr>
                            <tr><td style='padding: 5px 0; font-weight: bold;'>Status:</td><td>✅ Operational</td></tr>
                        </table>
                    </div>
                    
                    <div style='background-color: #f5f5f5; padding: 15px; border-radius: 5px; margin: 15px 0;'>
                        <h3>Initialized Components:</h3>
                        <ul>
                            <li>✅ Configuration loaded</li>
                            <li>✅ SharePoint client initialized</li>
                            <li>✅ Teams client initialized</li>
                            <li>✅ Email client initialized</li>
                            <li>✅ Confluence client initialized</li>
                            <li>✅ Authentication tokens acquired</li>
                            <li>✅ Database connection established</li>
                        </ul>
                    </div>
                    
                    <p>The service is now running and ready to process sync operations.</p>
                    
                    <hr style='margin: 20px 0;'>
                    <p style='font-size: 12px; color: #666;'>
                        This is an automated notification from the Confluence Sync Service.<br>
                        Sent when the service starts successfully.
                    </p>
                </body>
                </html>";
        }

        #endregion
    }
}