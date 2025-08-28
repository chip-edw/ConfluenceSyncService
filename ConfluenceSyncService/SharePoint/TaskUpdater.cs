using System.Net.Http.Json;
using Microsoft.Extensions.Options;


namespace ConfluenceSyncService.SharePoint
{
    public interface ISharePointTaskUpdater
    {
        Task<bool> MarkCompletedAsync(string listId, string itemId, string ackedBy, string? ackedByActual, CancellationToken ct);
        Task<bool> StampNotifiedAsync(string listId, string itemId, DateTime utc, CancellationToken ct);
        Task<bool> StampChaseAsync(string listId, string itemId, int chaseCount, DateTime nextChaseUtc, bool important, CancellationToken ct);

    }

    public sealed class SharePointTaskUpdater(HttpClient http, IOptions<SharePointFieldMappingsOptions> map) : ISharePointTaskUpdater
    {
        private readonly HttpClient _http = http;
        private readonly SharePointFieldMappingsOptions _map = map.Value;

        // Assumes your HttpClient is preconfigured with Graph base address and bearer token
        public async Task<bool> MarkCompletedAsync(string listId, string itemId, string ackedBy, string? ackedByActual, CancellationToken ct)
        {
            var fields = new Dictionary<string, object?>
            {
                [_map.Get("Status")] = "Completed",
                [_map.Get("CompletedDate")] = DateTime.UtcNow,
                [_map.Get("AckedBy")] = ackedBy,
                [_map.Get("AckedByActual")] = ackedByActual
            };

            return await PatchFieldsAsync(listId, itemId, fields, ct);
        }

        public Task<bool> StampNotifiedAsync(string listId, string itemId, DateTime utc, CancellationToken ct)
            => PatchFieldsAsync(listId, itemId, new()
            {
                [_map.Get("NotifiedAtUtc")] = utc
            }, ct);

        public Task<bool> StampChaseAsync(string listId, string itemId, int chaseCount, DateTime nextChaseUtc, bool important, CancellationToken ct)
            => PatchFieldsAsync(listId, itemId, new()
            {
                [_map.Get("ChaseCount")] = chaseCount,
                [_map.Get("NextChaseAtUtc")] = nextChaseUtc,
                [_map.Get("Important")] = important
            }, ct);


        private async Task<bool> PatchFieldsAsync(string listId, string itemId, Dictionary<string, object?> fields, CancellationToken ct)
        {
            var payload = new { fields };
            var resp = await _http.PatchAsJsonAsync($"/v1.0/sites/root/lists/{listId}/items/{itemId}/fields", payload, ct);

            return resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotModified;
        }
    }
}
