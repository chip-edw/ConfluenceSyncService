using System.Net.Http.Headers;
using System.Text.Json;
using ConfluenceSyncService.SharePoint;
using ConfluenceSyncService.Teams;
using Microsoft.Extensions.Options;

namespace ConfluenceSyncService.Hosted
{
    public sealed class ChaserOptions
    {
        public string ListId { get; set; } = "";         // Phase Tasks & Metadata (or Transition Assignments) list id (GUID)
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);
    }

    public sealed class ChaserService(
        IHttpClientFactory httpFactory,
        IGraphTokenProvider tokenProvider,
        INotificationService notifier,
        ISharePointTaskUpdater sp,
        IOptions<ChaserOptions> chaser,
        IOptions<SharePointFieldMappingsOptions> map,
        ILogger<ChaserService> log,
        IConfiguration config) // <-- read SiteId from config
        : BackgroundService
    {
        private readonly IHttpClientFactory _httpFactory = httpFactory;
        private readonly IGraphTokenProvider _tokens = tokenProvider;
        private readonly INotificationService _notifier = notifier;
        private readonly ISharePointTaskUpdater _sp = sp;
        private readonly ChaserOptions _opts = chaser.Value;
        private readonly SharePointFieldMappingsOptions _map = map.Value;
        private readonly ILogger<ChaserService> _log = log;

        private readonly string _siteId = config["SharePoint:Sites:0:SiteId"]
            ?? throw new InvalidOperationException("SharePoint:Sites:0:SiteId is missing.");

        private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await TickAsync(stoppingToken); }
                catch (Exception ex) { _log.LogError(ex, "Chaser tick failed"); }
                await Task.Delay(_opts.PollInterval, stoppingToken);
            }
        }

        private async Task TickAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_opts.ListId)) return;

            // Mapped field internal names
            var status = _map.Get("Status");
            var nextChase = _map.Get("NextChaseAtUtc");
            var important = _map.Get("Important");
            var chaseCount = _map.Get("ChaseCount");
            var notifiedAt = _map.Get("NotifiedAtUtc");
            var dueUtc = _map.Get("DueDateUtc");
            //var assigned = _map.TryGet("AssignedToAadUserId", out var a) ? a : null;

            // Use a format Graph likes for filters (no fractional seconds)
            var nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            var nowQ = $"'{nowIso}'"; // quote for fields/<date> comparisons

            // Build Graph URL (site by SiteId; select Id + expand fields with precise $select)
            var fieldsSelect = string.Join(",",
                new[] { status, nextChase, chaseCount, important, notifiedAt, dueUtc }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            var url =
                $"https://graph.microsoft.com/v1.0/sites/{_siteId}/lists/{_opts.ListId}/items" +
                $"?$select=Id" +
                $"&$expand=fields($select={fieldsSelect})" +
                // Status is a choice (string), Due/Next are stringly-typed in fieldValueSet -> quote them
                $"&$filter=(fields/{status} ne 'Completed')" +
                $" and (fields/{dueUtc} le {nowQ})" +
                $" and ((fields/{nextChase} eq null) or (fields/{nextChase} le {nowQ}))" +
                $"&$top=50";

            var resp = await GetGraphJsonAsync<GraphListItems>(url, ct);
            if (resp?.value is null || resp.value.Count == 0) return;

            foreach (var item in resp.value)
            {
                var id = item.id;
                var fields = item.fields ?? new();

                var cc = fields.GetInt(chaseCount) + 1;
                var due = fields.GetDate(dueUtc) ?? DateTime.UtcNow;
                var next = DateTime.UtcNow.AddHours(24);

                // string? mentionId = null; //optional: supply once AssignedToAadUserId exists
                //if (!string.IsNullOrWhiteSpace(assigned) && fields.TryGetString(assigned!, out var m))
                //    mentionId = m;

                // TODO: replace href with your signed link generator when wiring notification body
                var body = $@"<p><b>Reminder:</b> Task is past due since {due:u}. Click to acknowledge:
<a href=""#"" target=""_blank"">Open task</a></p>";

                //await _notifier.NotifyTaskAsync(_opts.ListId, id, body,
                //                                mentionId,
                //                                string.IsNullOrEmpty(mentionId) ? null : "@assignee",
                //                                ct);

                string? mentionId = null;      // until you add AssignedToAadUserId (or resolve by email)
                string? mentionText = null;    // keep null when there's no mentionId

                await _notifier.NotifyTaskAsync(_opts.ListId, id, body, mentionId, mentionText, ct);

                // Mark as important and schedule next chase in 24h
                await _sp.StampChaseAsync(_opts.ListId, id, cc, next, important: true, ct);
            }
        }

        // --- helpers ---

        private async Task<T?> GetGraphJsonAsync<T>(string url, CancellationToken ct)
        {
            var http = _httpFactory.CreateClient("graph");

            async Task<(HttpResponseMessage resp, string body)> send(bool allowNonIndexed)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                var token = await _tokens.GetTokenAsync(ct);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (allowNonIndexed)
                    req.Headers.TryAddWithoutValidation("Prefer", "HonorNonIndexedQueriesWarningMayFailRandomly");

                var resp = await http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (resp, body);
            }

            var (resp, body) = await send(allowNonIndexed: false);

            // Retry once with Prefer header if Graph complains about non-indexed filter/orderby
            if ((int)resp.StatusCode == 400 &&
                body.IndexOf("not indexed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _log.LogWarning("Non-indexed filter detected; retrying with Prefer header.");
                (resp, body) = await send(allowNonIndexed: true);
            }

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogError("Graph GET failed ({Code} {Reason}) for {Url}. Body: {Body}",
                    (int)resp.StatusCode, resp.ReasonPhrase, url, body);
                throw new HttpRequestException($"Graph GET failed {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }

            return JsonSerializer.Deserialize<T>(body, _json);
        }


        private sealed class GraphListItems { public List<GraphItem> value { get; set; } = new(); }
        private sealed class GraphItem { public string id { get; set; } = ""; public Dictionary<string, object>? fields { get; set; } }
    }

    file static class FieldExt
    {
        public static bool TryGetString(this Dictionary<string, object> f, string key, out string? val)
        {
            val = null;
            if (!f.TryGetValue(key, out var o) || o is null) return false;
            val = o.ToString();
            return !string.IsNullOrWhiteSpace(val);
        }
        public static int GetInt(this Dictionary<string, object> f, string key)
            => f.TryGetValue(key, out var o) && o is not null && int.TryParse(o.ToString(), out var i) ? i : 0;
        public static DateTime? GetDate(this Dictionary<string, object> f, string key)
            => f.TryGetValue(key, out var o) && o is not null && DateTime.TryParse(o.ToString(), out var d) ? d : null;
    }

    file static class MapExt
    {
        public static bool TryGet(this SharePointFieldMappingsOptions map, string logicalName, out string? value)
        {
            try
            {
                value = map.Get(logicalName);
                return !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                value = null;
                return false;
            }
        }
    }

}
