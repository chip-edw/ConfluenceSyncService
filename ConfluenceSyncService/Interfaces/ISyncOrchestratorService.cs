namespace ConfluenceSyncService.Interfaces
{
    public interface ISyncOrchestratorService
    {
        Task RunSyncAsync(CancellationToken cancellationToken);
    }
}
