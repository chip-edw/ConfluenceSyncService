namespace ConfluenceSyncService.Services.State
{
    public interface ICursorStore
    {
        Task<string?> GetAsync(string key, CancellationToken ct = default);
        Task SetAsync(string key, string value, CancellationToken ct = default);
    }
}
