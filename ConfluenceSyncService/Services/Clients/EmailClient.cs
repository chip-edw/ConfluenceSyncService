using ConfluenceSyncService.MSGraphAPI;
using Newtonsoft.Json;
using Serilog;
using System.Net.Http.Headers;
using System.Text;

namespace ConfluenceSyncService.Services.Clients
{
    public class EmailClient
    {
        private readonly HttpClient _httpClient;
        private readonly ConfidentialClientApp _confidentialClientApp;
        private readonly IConfiguration _configuration;
        private readonly Serilog.ILogger _logger;

        public EmailClient(HttpClient httpClient, ConfidentialClientApp confidentialClientApp, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _confidentialClientApp = confidentialClientApp;
            _configuration = configuration;
            _logger = Log.ForContext<EmailClient>();
        }

        #region Core Email Methods

        /// <summary>
        /// Sends an email using Microsoft Graph API with delegated permissions
        /// </summary>
        public async Task<string> SendEmailAsync(string toEmail, string subject, string body, bool isHtml = false, string? fromEmail = null, string? fromDisplayName = null)
        {
            _logger.Information("Sending email to {ToEmail} with subject: {Subject}", toEmail, subject);

            try
            {
                // Use configured sender or default to the service account
                var senderEmail = fromEmail ?? GetConfiguredSenderEmail();
                var senderDisplayName = fromDisplayName ?? GetConfiguredDisplayName();

                // Get delegated token for the sender email account
                var accessToken = await _confidentialClientApp.GetAccessToken();

                // Use /me/sendMail endpoint since we're authenticated as the sender
                var url = "https://graph.microsoft.com/v1.0/me/sendMail";

                var emailPayload = new
                {
                    message = new
                    {
                        subject = subject,
                        body = new
                        {
                            contentType = isHtml ? "HTML" : "Text",
                            content = body
                        },
                        from = new
                        {
                            emailAddress = new
                            {
                                address = senderEmail,
                                name = senderDisplayName
                            }
                        },
                        toRecipients = new[]
                        {
                    new
                    {
                        emailAddress = new
                        {
                            address = toEmail
                        }
                    }
                }
                    }
                };

                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Content = new StringContent(JsonConvert.SerializeObject(emailPayload), Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.Error("Failed to send email: {StatusCode} - {Error}", response.StatusCode, errorContent);
                    throw new HttpRequestException($"Failed to send email: {response.StatusCode} - {errorContent}");
                }

                _logger.Information("Email sent successfully to {ToEmail} from {FromDisplayName}", toEmail, senderDisplayName);
                return "Email sent successfully";
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error sending email to {ToEmail}", toEmail);
                throw;
            }
        }



        /// <summary>
        /// Sends an email using configured email settings from appsettings.json
        /// </summary>
        public async Task<string> SendNotificationEmailAsync(string toEmail, string subject, string body, bool isHtml = false, string? notificationType = null)
        {
            var emailConfig = GetEmailConfiguration(notificationType);

            return await SendEmailAsync(toEmail, subject, body, isHtml, emailConfig.FromEmail, emailConfig.FromDisplayName);
        }

        /// <summary>
        /// Sends a task notification email (dynamic recipient)
        /// </summary>
        public async Task<string> SendTaskNotificationAsync(string assigneeEmail, string subject, string body, bool isHtml = true)
        {
            return await SendNotificationEmailAsync(assigneeEmail, subject, body, isHtml, "TaskNotification");
        }

        /// <summary>
        /// Sends an alert email (fixed recipient from config)
        /// </summary>
        public async Task<string> SendAlertAsync(string subject, string body, bool isHtml = false)
        {
            var emailConfig = GetEmailConfiguration("Alert");

            if (string.IsNullOrEmpty(emailConfig.ToEmail))
            {
                throw new InvalidOperationException("Alert notification ToEmail is not configured");
            }

            return await SendEmailAsync(emailConfig.ToEmail, subject, body, isHtml, emailConfig.FromEmail, emailConfig.FromDisplayName);
        }

        #endregion

        #region Configuration Methods

        /// <summary>
        /// Gets email configuration from appsettings.json
        /// </summary>
        private EmailConfiguration GetEmailConfiguration(string? notificationType = null)
        {
            var emailSection = _configuration.GetSection("Email");

            if (!emailSection.Exists())
            {
                throw new InvalidOperationException("Email configuration section is missing from appsettings.json");
            }

            var config = new EmailConfiguration
            {
                FromEmail = emailSection["FromEmail"],
                FromDisplayName = emailSection["FromDisplayName"]
            };

            // If a specific notification type is requested, override with its settings
            if (!string.IsNullOrEmpty(notificationType))
            {
                var notificationConfig = emailSection.GetSection($"Notifications:{notificationType}");
                if (notificationConfig.Exists())
                {
                    // Override display name if specified
                    config.FromDisplayName = notificationConfig["FromDisplayName"] ?? config.FromDisplayName;

                    // Get ToEmail if specified (for alerts)
                    config.ToEmail = notificationConfig["ToEmail"];
                }
            }

            if (string.IsNullOrEmpty(config.FromEmail))
            {
                throw new InvalidOperationException("Email configuration must specify FromEmail address");
            }

            return config;
        }

        private string GetConfiguredSenderEmail()
        {
            var emailSection = _configuration.GetSection("Email");
            var fromEmail = emailSection["FromEmail"];

            if (string.IsNullOrEmpty(fromEmail))
            {
                throw new InvalidOperationException("No sender email configured. Set Email:FromEmail in appsettings.json");
            }

            return fromEmail;
        }

        private string GetConfiguredDisplayName()
        {
            var emailSection = _configuration.GetSection("Email");
            return emailSection["FromDisplayName"] ?? "Confluence Sync Service";
        }

        #endregion

        #region Token Validation (same pattern as Teams/SharePoint)

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

    public class EmailConfiguration
    {
        public string? FromEmail { get; set; }
        public string ToEmail { get; set; } = "";
        public string? FromDisplayName { get; set; }
    }

    #endregion
}