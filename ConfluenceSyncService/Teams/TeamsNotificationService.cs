using ConfluenceSyncService.SharePoint;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace ConfluenceSyncService.Teams
{
    public sealed class TeamsOptions
    {
        public string TeamId { get; set; } = "";
        public string ChannelId { get; set; } = "";
    }

    public interface IGraphTokenProvider
    {
        Task<string> GetTokenAsync(CancellationToken ct);
    }

    public interface INotificationService
    {
        Task NotifyTaskAsync(string listId, string itemId, string htmlBody, string? mentionUserObjectId, string? mentionText, CancellationToken ct);
    }

    public sealed class TeamsNotificationService(
        IHttpClientFactory httpFactory,
        IOptions<TeamsOptions> teams,
        IGraphTokenProvider tokenProvider,
        ISharePointTaskUpdater sp)
        : INotificationService
    {
        private readonly TeamsOptions _opts = teams.Value;
        private readonly IHttpClientFactory _httpFactory = httpFactory;
        private readonly IGraphTokenProvider _tokens = tokenProvider;
        private readonly ISharePointTaskUpdater _sp = sp;

        public async Task NotifyTaskAsync(string listId, string itemId, string htmlBody, string? mentionUserObjectId, string? mentionText, CancellationToken ct)
        {
            var http = _httpFactory.CreateClient("graph");
            var token = await _tokens.GetTokenAsync(ct);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Try to extract the first link from the HTML to use in an Adaptive Card action.
            // We assume the caller already embedded the signed ACK URL in htmlBody.
            var ackUrl = TryExtractFirstHref(htmlBody);

            // Build the Graph payload: HTML body + optional mentions + optional Adaptive Card
            var payload = BuildGraphPayload(htmlBody, mentionUserObjectId, mentionText, ackUrl);

            var resp = await http.PostAsJsonAsync($"/v1.0/teams/{_opts.TeamId}/channels/{_opts.ChannelId}/messages", payload, ct);
            resp.EnsureSuccessStatusCode();

            // (Optional) you could read message id here if you want it later:
            // using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            // var messageId = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

            await _sp.StampNotifiedAsync(listId, itemId, DateTime.UtcNow, ct);
        }

        private static string? TryExtractFirstHref(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            // Simple, robust-enough regex for <a href="...">; avoids heavy HTML parsing.
            // It looks for the first double-quoted href.
            var m = Regex.Match(html, "<a\\s+[^>]*href\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (m.Success && m.Groups.Count > 1)
            {
                var url = m.Groups[1].Value;
                // very shallow sanity check
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }
            }
            return null;
        }

        private static object BuildGraphPayload(string htmlBody, string? mentionUserObjectId, string? mentionText, string? ackUrl)
        {
            var body = new { contentType = "html", content = htmlBody };

            object? mentions = null;
            if (!string.IsNullOrWhiteSpace(mentionUserObjectId) && !string.IsNullOrWhiteSpace(mentionText))
            {
                mentions = new[]
                {
                    new
                    {
                        id = 0,
                        mentionText = mentionText,
                        mentioned = new { user = new { id = mentionUserObjectId } }
                    }
                };
            }

            object? attachments = null;
            if (!string.IsNullOrEmpty(ackUrl))
            {
                // Minimal Adaptive Card with a Mark Complete button.
                var card = new Dictionary<string, object?>
                {
                    ["type"] = "AdaptiveCard",
                    ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                    ["version"] = "1.4",
                    ["body"] = new object[]
                    {
                        new { type = "TextBlock", text = "Task Notification", weight = "Bolder", size = "Medium" },
                        new { type = "TextBlock", text = "Use the button below to mark complete.", wrap = true, spacing = "Small" }
                    },
                    ["actions"] = new object[]
                    {
        new { type = "Action.OpenUrl", title = "✅ Mark Complete", url = ackUrl }
                    }
                };


                attachments = new object[]
                {
                    new
                    {
                        contentType = "application/vnd.microsoft.card.adaptive",
                        content = card
                    }
                };
            }

            if (mentions is not null && attachments is not null)
                return new { body, mentions, attachments };
            if (mentions is not null)
                return new { body, mentions };
            if (attachments is not null)
                return new { body, attachments };

            return new { body };
        }
    }
}
