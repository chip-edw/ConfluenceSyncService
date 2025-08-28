using System.Net.Http.Headers;
using System.Net.Http.Json;
using ConfluenceSyncService.SharePoint;
using Microsoft.Extensions.Options;

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

            object payload;
            if (!string.IsNullOrWhiteSpace(mentionUserObjectId) && !string.IsNullOrWhiteSpace(mentionText))
            {
                payload = new
                {
                    body = new { contentType = "html", content = htmlBody },
                    mentions = new[]
                        {
                    new
                    {
                        id = 0,
                        mentionText = mentionText,
                        mentioned = new { user = new { id = mentionUserObjectId } }
                    }
                }
                };
            }
            else
            {
                payload = new { body = new { contentType = "html", content = htmlBody } };
            }

            var resp = await http.PostAsJsonAsync($"/v1.0/teams/{_opts.TeamId}/channels/{_opts.ChannelId}/messages", payload, ct);
            resp.EnsureSuccessStatusCode();

            await _sp.StampNotifiedAsync(listId, itemId, DateTime.UtcNow, ct);
        }
    }
}
