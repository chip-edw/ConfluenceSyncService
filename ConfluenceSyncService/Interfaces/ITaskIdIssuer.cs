namespace ConfluenceSyncService.Interfaces
{
    public interface ITaskIdIssuer
    {
        Task<int> ReserveAsync(
            string listKey,
            string? correlationId,
            string? customerId,
            string? phaseName,
            string? taskName,
            string? workflowId,
            CancellationToken ct = default);

        Task LinkToSharePointAsync(
            int taskId,
            string spItemId,
            CancellationToken ct = default);
    }
}
