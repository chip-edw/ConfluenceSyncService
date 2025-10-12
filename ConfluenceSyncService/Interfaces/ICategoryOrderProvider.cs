namespace ConfluenceSyncService.Interfaces
{
    public interface ICategoryOrderProvider
    {
        ValueTask LoadAsync(CancellationToken ct = default);
        IReadOnlyDictionary<string, int> GetMap();
    }
}
