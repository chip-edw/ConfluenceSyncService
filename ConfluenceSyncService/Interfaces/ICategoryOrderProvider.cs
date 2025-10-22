namespace ConfluenceSyncService.Interfaces
{
    public interface ICategoryOrderProvider
    {
        ValueTask LoadAsync(CancellationToken ct = default);
        IReadOnlyDictionary<(string Category, string AnchorDateType), int> GetMap();
    }
}
