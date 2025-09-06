using ConfluenceSyncService.MSGraphAPI;
using ConfluenceSyncService.SharePoint;
using Serilog;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConfluenceSyncService.Teams
{
    public interface IGraphTokenProvider
    {
        Task<string> GetTokenAsync(CancellationToken ct);
    }

    public interface INotificationService
    {
        /// <summary>
        /// Posts an initial channel message (root post) with the given HTML body.
        /// If the body contains a link, the first <a href="..."> is extracted and added as an Adaptive Card button.
        /// Also stamps NotifiedAtUtc on the SharePoint item.
        /// </summary>
        Task NotifyTaskAsync(string listId, string itemId, string htmlBody, string? mentionUserObjectId, string? mentionText, CancellationToken ct);
        Task<bool> PostChaserAsync(string teamId, string channelId, string rootMessageId,
            string overdueText, string ackUrl, string threadFallback, CancellationToken ct);
    }

    /// <summary>
    /// Teams channel notifier:
    ///  - Initial root posts (HTML + optional mentions + minimal Adaptive Card with "✅ Mark Complete")
    ///  - C2 chasers: reply to an existing thread with (1) short OVERDUE text, (2) Adaptive Card with fresh ACK link.
    ///    If the root message id is missing and threadFallback == "RootNew", it starts a new thread (root text + reply card).
    /// </summary>
    public sealed class TeamsNotificationService(
        IHttpClientFactory httpFactory,
        Microsoft.Extensions.Options.IOptions<ConfluenceSyncService.Options.TeamsOptions> teams,
        ITeamsGraphTokenProvider tokenProvider,
        ISharePointTaskUpdater sp)
        : INotificationService
    {
        private readonly ConfluenceSyncService.Options.TeamsOptions _opts = teams.Value;
        private readonly IHttpClientFactory _httpFactory = httpFactory;
        private readonly ITeamsGraphTokenProvider _tokens = tokenProvider;
        private readonly ISharePointTaskUpdater _sp = sp;

        // ---------------------------
        // Public API (existing C1)
        // ---------------------------

        public async Task NotifyTaskAsync(string listId, string itemId, string htmlBody, string? mentionUserObjectId, string? mentionText, CancellationToken ct)
        {
            var http = await CreateGraphClientAsync(ct);

            // Try to extract the first ACK link from the supplied HTML.
            var ackUrl = TryExtractFirstHref(htmlBody);

            // Build payload and post root message to the configured team/channel.
            var payload = BuildGraphPayload(htmlBody, mentionUserObjectId, mentionText, ackUrl);
            var resp = await http.PostAsJsonAsync($"/v1.0/teams/{_opts.TeamId}/channels/{_opts.ChannelId}/messages", payload, ct);
            resp.EnsureSuccessStatusCode();

            // Stamp NotifiedAtUtc on the SharePoint item.
            await _sp.StampNotifiedAsync(listId, itemId, DateTime.UtcNow, ct);
        }

        // ---------------------------
        // Public API (C2 additions)
        // ---------------------------

        /// <summary>
        /// C2: Post an overdue chaser into an existing thread (root message), as two replies:
        ///   1) Short plain-text "OVERDUE" message
        ///   2) Minimal Adaptive Card with a "✅ Mark Complete" button that opens ackUrl
        /// If rootMessageId is missing or invalid and threadFallback == "RootNew", starts a new root with the text
        /// and then replies with the card. Returns true if both posts succeed; false if skipped or failed.
        /// </summary>
        public async Task<bool> PostChaserAsync(
            string? teamId,
            string? channelId,
            string? rootMessageId,
            string overdueText,
            string ackUrl,
            string threadFallback,
            CancellationToken ct)
        {
            var http = await CreateGraphClientAsync(ct);

            // Use per-task overrides if provided; otherwise fall back to configured defaults.
            var team = string.IsNullOrWhiteSpace(teamId) ? _opts.TeamId : teamId!;
            var channel = string.IsNullOrWhiteSpace(channelId) ? _opts.ChannelId : channelId!;

            Log.Information("PostChaserAsync called: TeamId={TeamId}, ChannelId={ChannelId}, RootMessageId={RootMessageId}, " +
                "ThreadFallback={ThreadFallback}", team, channel, rootMessageId, threadFallback);

            // If we have a valid thread root, try to reply twice (text + card).
            if (!string.IsNullOrWhiteSpace(rootMessageId))
            {
                // 1) Reply with short text
                var textPayload = BuildPlainTextPayload(overdueText);
                var replyUrl = $"/v1.0/teams/{team}/channels/{channel}/messages/{rootMessageId}/replies";

                var textResp = await http.PostAsJsonAsync(replyUrl, textPayload, ct);
                if (!textResp.IsSuccessStatusCode)
                {
                    var errorContent = await textResp.Content.ReadAsStringAsync(ct);
                    Log.Error("Teams text reply failed: Status={Status}, Error={ErrorContent}, URL={URL}, TeamId={TeamId}, ChannelId={ChannelId}, RootMessageId={RootMessageId}",
                           textResp.StatusCode, errorContent, replyUrl, team, channel, rootMessageId);

                    if (string.Equals(threadFallback, "RootNew", StringComparison.OrdinalIgnoreCase))
                    {
                        return await PostChaserWithRootFallbackAsync(http, team, channel, overdueText, ackUrl, ct);
                    }
                    return false;
                }

                // 2) Reply with minimal Adaptive Card (Mark Complete)
                var cardPayload = BuildAdaptiveCardReplyPayload(ackUrl);
                var cardResp = await http.PostAsJsonAsync(replyUrl, cardPayload, ct);
                if (!cardResp.IsSuccessStatusCode)
                {
                    var errorContent = await cardResp.Content.ReadAsStringAsync(ct);
                    Log.Error("Teams card reply failed: Status={Status}, Error={ErrorContent}",
                        cardResp.StatusCode, errorContent);
                }
                return cardResp.IsSuccessStatusCode;
                return cardResp.IsSuccessStatusCode;
            }

            // No root message id. Honor fallback policy.
            if (string.Equals(threadFallback, "RootNew", StringComparison.OrdinalIgnoreCase))
            {
                return await PostChaserWithRootFallbackAsync(http, team, channel, overdueText, ackUrl, ct);
            }

            // Skip if we aren't allowed to create a new thread.
            return false;
        }

        // ---------------------------
        // Internal helpers
        // ---------------------------

        private async Task<HttpClient> CreateGraphClientAsync(CancellationToken ct)
        {
            var http = _httpFactory.CreateClient("graph");
            var token = await _tokens.GetTokenAsync(ct);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Log.Information("Making Teams API call with token. BaseAddress: {BaseAddress}", http.BaseAddress);
            return http;
        }

        /// <summary>
        /// Fallback path when replying into a thread fails or no rootMessageId is provided:
        /// Creates a new root post with the overdue text, parses its message id,
        /// then posts the Adaptive Card as a reply to that new root.
        /// </summary>
        private static async Task<bool> PostChaserWithRootFallbackAsync(HttpClient http, string team, string channel, string overdueText, string ackUrl, CancellationToken ct)
        {
            Log.Information("PostChaserWithRootFallbackAsync: Creating new root message in team={TeamId}, channel={ChannelId}", team, channel);

            // Root message: plain text (keep it simple and visible)
            var rootPayload = BuildPlainTextPayload(overdueText);
            var rootUrl = $"/v1.0/teams/{team}/channels/{channel}/messages";

            Log.Information("Posting root message to URL: {URL}", rootUrl);
            var rootResp = await http.PostAsJsonAsync(rootUrl, rootPayload, ct);

            if (!rootResp.IsSuccessStatusCode)
            {
                var errorContent = await rootResp.Content.ReadAsStringAsync(ct);
                Log.Error("Root message creation failed: Status={Status}, Error={ErrorContent}, URL={URL}",
                    rootResp.StatusCode, errorContent, rootUrl);
                return false;
            }

            Log.Information("Root message created successfully, parsing response for message ID");

            string? newRootId = null;
            await using (var s = await rootResp.Content.ReadAsStreamAsync(ct))
            {
                using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                    newRootId = idProp.GetString();
            }

            if (string.IsNullOrWhiteSpace(newRootId))
            {
                Log.Error("Failed to extract message ID from root message response");
                return false;
            }

            Log.Information("Got new root message ID: {MessageId}, posting card reply", newRootId);

            // Reply to the new root with the card
            var replyUrl = $"/v1.0/teams/{team}/channels/{channel}/messages/{newRootId}/replies";
            var cardPayload = BuildAdaptiveCardReplyPayload(ackUrl);
            var cardResp = await http.PostAsJsonAsync(replyUrl, cardPayload, ct);

            if (!cardResp.IsSuccessStatusCode)
            {
                var errorContent = await cardResp.Content.ReadAsStringAsync(ct);
                Log.Error("Card reply failed: Status={Status}, Error={ErrorContent}, URL={URL}",
                    cardResp.StatusCode, errorContent, replyUrl);
            }
            else
            {
                Log.Information("Card reply posted successfully");
            }

            return cardResp.IsSuccessStatusCode;
        }
        private static string? TryExtractFirstHref(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            // Simple regex for the first <a href="..."> (double-quoted)
            var m = Regex.Match(html, "<a\\s+[^>]*href\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (m.Success && m.Groups.Count > 1)
            {
                var url = m.Groups[1].Value;
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }
            }
            return null;
        }

        /// <summary>
        /// Root post payload builder (HTML body + optional mentions + optional adaptive card attachment).
        /// </summary>
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
                var card = BuildAdaptiveCardContent("Task Notification", "Use the button below to mark complete.", ackUrl);
                attachments = new object[]
                {
                    new { contentType = "application/vnd.microsoft.card.adaptive", content = card }
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

        /// <summary>
        /// Reply payload: plain text only (for the OVERDUE visibility ping).
        /// </summary>
        private static object BuildPlainTextPayload(string text)
        {
            return new
            {
                body = new
                {
                    contentType = "text",
                    content = text
                }
            };
        }

        /// <summary>
        /// Reply payload: minimal Adaptive Card with single "✅ Mark Complete" button (for chasers).
        /// </summary>
        private static object BuildAdaptiveCardReplyPayload(string ackUrl)
        {
            // Skip adaptive card entirely, just post a simple text message with clickable link
            return new
            {
                body = new
                {
                    contentType = "html",
                    content = $"<p>Action required - please review the task above.</p><p><a href=\"{ackUrl}\">Click here to mark complete</a></p>"
                }
                // No attachments - just a simple HTML message with clickable link
            };
        }

        /// <summary>
        /// Minimal Adaptive Card (1.4) with a single OpenUrl action to the provided ackUrl.
        /// </summary>
        private static object BuildAdaptiveCardContent(string title, string subText, string ackUrl)
        {
            return new
            {
                type = "AdaptiveCard",
                version = "1.2", // Try older version
                body = new[]
                {
            new
            {
                type = "TextBlock",
                text = title
            },
            new
            {
                type = "TextBlock",
                text = subText
            }
        },
                actions = new[]
                {
            new
            {
                type = "Action.OpenUrl",
                title = "Mark Complete",
                url = ackUrl
            }
        }
            };
        }
    }
}
