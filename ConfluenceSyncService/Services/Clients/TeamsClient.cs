using ConfluenceSyncService.MSGraphAPI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;

namespace ConfluenceSyncService.Services.Clients
{
    public class TeamsClient
    {
        private readonly HttpClient _httpClient;
        private readonly ConfidentialClientApp _confidentialClientApp;
        private readonly IConfiguration _configuration;
        private readonly Serilog.ILogger _logger;
        private readonly ConcurrentDictionary<string, string> _teamsCache = new();

        public TeamsClient(HttpClient httpClient, ConfidentialClientApp confidentialClientApp, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _confidentialClientApp = confidentialClientApp;
            _configuration = configuration;
            _logger = Log.ForContext<TeamsClient>();
        }

        #region Discovery Methods

        /// <summary>
        /// Lists all Teams groups that the app has access to
        /// </summary>
        public async Task<List<TeamsGroup>> GetAllTeamsGroupsAsync()
        {
            _logger.Information("Getting all Teams groups");

            try
            {
                var url = "https://graph.microsoft.com/v1.0/groups?$filter=resourceProvisioningOptions/Any(x:x eq 'Team')&$select=id,displayName,description,mailNickname";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.Error("Failed to get Teams groups: {StatusCode} - {Error}", response.StatusCode, errorContent);
                    throw new HttpRequestException($"Failed to get Teams groups: {response.StatusCode} - {errorContent}");
                }

                var content = await response.Content.ReadAsStringAsync();
                var json = JObject.Parse(content);

                var teams = new List<TeamsGroup>();

                foreach (var team in json["value"] ?? Enumerable.Empty<JToken>())
                {
                    teams.Add(new TeamsGroup
                    {
                        Id = team["id"]?.ToString() ?? "",
                        DisplayName = team["displayName"]?.ToString() ?? "",
                        Description = team["description"]?.ToString() ?? "",
                        MailNickname = team["mailNickname"]?.ToString() ?? ""
                    });
                }

                _logger.Information("Found {Count} Teams groups", teams.Count);
                return teams;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error getting Teams groups");
                throw;
            }
        }

        /// <summary>
        /// Gets all channels for a specific team
        /// </summary>
        public async Task<List<TeamsChannel>> GetTeamChannelsAsync(string teamId)
        {
            _logger.Information("Getting channels for team {TeamId}", teamId);

            try
            {
                var url = $"https://graph.microsoft.com/v1.0/teams/{teamId}/channels";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.Error("Failed to get channels for team {TeamId}: {StatusCode} - {Error}", teamId, response.StatusCode, errorContent);
                    throw new HttpRequestException($"Failed to get channels for team {teamId}: {response.StatusCode} - {errorContent}");
                }

                var content = await response.Content.ReadAsStringAsync();
                var json = JObject.Parse(content);

                var channels = new List<TeamsChannel>();

                foreach (var channel in json["value"] ?? Enumerable.Empty<JToken>())
                {
                    channels.Add(new TeamsChannel
                    {
                        Id = channel["id"]?.ToString() ?? "",
                        DisplayName = channel["displayName"]?.ToString() ?? "",
                        Description = channel["description"]?.ToString() ?? "",
                        WebUrl = channel["webUrl"]?.ToString() ?? "",
                        MembershipType = channel["membershipType"]?.ToString() ?? ""
                    });
                }

                _logger.Information("Found {Count} channels for team {TeamId}", channels.Count, teamId);
                return channels;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error getting channels for team {TeamId}", teamId);
                throw;
            }
        }


        /// <summary>
        /// Comprehensive discovery method that gets teams, channels, and chats
        /// </summary>
        public async Task<TeamsDiscoveryResult> DiscoverAllTeamsResourcesAsync()
        {
            _logger.Information("Starting comprehensive Teams discovery");

            var result = new TeamsDiscoveryResult
            {
                Teams = new List<TeamsGroupWithChannels>(),
                Chats = new List<TeamsChat>()
            };

            try
            {
                // Get all teams
                var teams = await GetAllTeamsGroupsAsync();

                // Get channels for each team
                foreach (var team in teams)
                {
                    try
                    {
                        var channels = await GetTeamChannelsAsync(team.Id);
                        result.Teams.Add(new TeamsGroupWithChannels
                        {
                            Team = team,
                            Channels = channels
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Failed to get channels for team {TeamId} ({TeamName})", team.Id, team.DisplayName);
                        result.Teams.Add(new TeamsGroupWithChannels
                        {
                            Team = team,
                            Channels = new List<TeamsChannel>()
                        });
                    }
                }

                _logger.Information("Discovery complete: {TeamCount} teams", result.Teams.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error during Teams discovery");
                throw;
            }
        }

        #endregion

        #region Messaging Methods

        /// <summary>
        /// Sends a message to a specific Teams channel
        /// </summary>
        public async Task<string> SendChannelMessageAsync(string teamId, string channelId, string message, string? subject = null)
        {
            _logger.Information("Sending message to channel {ChannelId} in team {TeamId}", channelId, teamId);

            try
            {
                var url = $"https://graph.microsoft.com/v1.0/teams/{teamId}/channels/{channelId}/messages";

                var payload = new
                {
                    body = new
                    {
                        content = message,
                        contentType = "text"
                    },
                    subject = subject
                };

                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());
                request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.Error("Failed to send channel message: {StatusCode} - {Error}", response.StatusCode, errorContent);
                    throw new HttpRequestException($"Failed to send channel message: {response.StatusCode} - {errorContent}");
                }

                var content = await response.Content.ReadAsStringAsync();
                var json = JObject.Parse(content);
                var messageId = json["id"]?.ToString();

                _logger.Information("Successfully sent message to channel. Message ID: {MessageId}", messageId);
                return messageId ?? "";
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error sending channel message");
                throw;
            }
        }

        /// <summary>
        /// Sends a message to a specific chat
        /// </summary>
        public async Task<string> SendChatMessageAsync(string chatId, string message)
        {
            _logger.Information("Sending message to chat {ChatId}", chatId);

            try
            {
                var url = $"https://graph.microsoft.com/v1.0/chats/{chatId}/messages";

                var payload = new
                {
                    body = new
                    {
                        content = message,
                        contentType = "text"
                    }
                };

                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());
                request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.Error("Failed to send chat message: {StatusCode} - {Error}", response.StatusCode, errorContent);
                    throw new HttpRequestException($"Failed to send chat message: {response.StatusCode} - {errorContent}");
                }

                var content = await response.Content.ReadAsStringAsync();
                var json = JObject.Parse(content);
                var messageId = json["id"]?.ToString();

                _logger.Information("Successfully sent message to chat. Message ID: {MessageId}", messageId);
                return messageId ?? "";
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error sending chat message");
                throw;
            }
        }

        /// <summary>
        /// Sends a message using configured Teams settings from appsettings.json
        /// </summary>
        public async Task<string> SendNotificationAsync(string message, string? subject = null)
        {
            var teamsConfig = GetTeamsConfiguration();

            return await SendChannelMessageAsync(teamsConfig.TeamId!, teamsConfig.ChannelId!, message, subject);
        }

        #endregion

        #region Configuration Methods

        /// <summary>
        /// Gets Teams configuration from startup cache
        /// </summary>
        private TeamsConfiguration GetTeamsConfiguration()
        {
            var config = StartupConfiguration.TeamsConfiguration;

            if (config == null)
            {
                throw new InvalidOperationException("Teams configuration is not loaded. Ensure LoadAllStartupDataAsync() was called during startup.");
            }

            return new TeamsConfiguration
            {
                IsChannelNotification = true,
                TeamId = config.TeamId,
                ChannelId = config.ChannelId
            };
        }

        private TeamsConfiguration BuildTeamsConfiguration(IConfigurationSection section)
        {
            var config = new TeamsConfiguration();

            var teamId = section["TeamId"];
            var channelId = section["ChannelId"];

            if (!string.IsNullOrEmpty(teamId) && !string.IsNullOrEmpty(channelId))
            {
                config.IsChannelNotification = true;
                config.TeamId = teamId;
                config.ChannelId = channelId;
                config.Subject = section["Subject"];
            }
            else
            {
                throw new InvalidOperationException("Teams configuration must specify both TeamId and ChannelId");
            }

            return config;
        }

        /// <summary>
        /// Loads and caches Teams configuration on startup
        /// </summary>
        //public void LoadTeamsConfiguration()
        //{
        //    _logger.Information("Loading Teams configuration from appsettings.json");

        //    var teamsSection = _configuration.GetSection("Teams");
        //    if (!teamsSection.Exists())
        //    {
        //        _logger.Warning("No Teams configuration section found in appsettings.json");
        //        return;
        //    }

        //    // Load default configuration
        //    var defaultSection = teamsSection.GetSection("Default");
        //    if (defaultSection.Exists())
        //    {
        //        var defaultConfig = BuildTeamsConfiguration(defaultSection);
        //        _logger.Information("Loaded default Teams configuration: {Type}",
        //            defaultConfig.IsChannelNotification ? "Channel" : "Chat");
        //    }

        //    // Load notification-specific configurations
        //    var notificationsSection = teamsSection.GetSection("Notifications");
        //    if (notificationsSection.Exists())
        //    {
        //        foreach (var notificationConfig in notificationsSection.GetChildren())
        //        {
        //            try
        //            {
        //                var config = BuildTeamsConfiguration(notificationConfig);
        //                _logger.Information("Loaded Teams notification configuration for '{NotificationType}': {Type}",
        //                    notificationConfig.Key, config.IsChannelNotification ? "Channel" : "Chat");
        //            }
        //            catch (Exception ex)
        //            {
        //                _logger.Warning(ex, "Failed to load Teams configuration for notification type '{NotificationType}'", notificationConfig.Key);
        //            }
        //        }
        //    }
        //}

        #endregion

        #region Token Validation (same pattern as SharePoint)

        /// <summary>
        /// Token validation method for orchestrator
        /// </summary>
        public async Task<bool> ValidateTokenAsync()
        {
            try
            {
                // Simple token validation - try to make a basic Graph API call
                var url = "https://graph.microsoft.com/v1.0/me";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _confidentialClientApp.GetAccessToken());

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Token refresh method for orchestrator
        /// </summary>
        public async Task RefreshTokenAsync()
        {
            // Token refresh is handled by ConfidentialClientApp.GetAccessToken()
            // It automatically refreshes if needed
            await _confidentialClientApp.GetAccessToken();
        }

        #endregion
    }

    #region Supporting Classes

    public class TeamsGroup
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public string MailNickname { get; set; } = "";
    }

    public class TeamsChannel
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public string WebUrl { get; set; } = "";
        public string MembershipType { get; set; } = "";
    }

    public class TeamsChat
    {
        public string Id { get; set; } = "";
        public string Topic { get; set; } = "";
        public string ChatType { get; set; } = "";
        public string WebUrl { get; set; } = "";
        public DateTime CreatedDateTime { get; set; }
    }

    public class TeamsGroupWithChannels
    {
        public TeamsGroup Team { get; set; } = new();
        public List<TeamsChannel> Channels { get; set; } = new();
    }

    public class TeamsDiscoveryResult
    {
        public List<TeamsGroupWithChannels> Teams { get; set; } = new();
        public List<TeamsChat> Chats { get; set; } = new();
    }

    public class TeamsConfiguration
    {
        public bool IsChannelNotification { get; set; }
        public string? TeamId { get; set; }
        public string? ChannelId { get; set; }
        public string? ChatId { get; set; }
        public string? Subject { get; set; }
    }

    #endregion
}