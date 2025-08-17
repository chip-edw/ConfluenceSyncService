namespace ConfluenceSyncService.Services.Workflow
{
    public interface IWorkflowMappingProvider
    {
        /// <summary>Load and cache the workflow mapping. Safe to call multiple times.</summary>
        ValueTask LoadAsync(CancellationToken ct = default);

        /// <summary>Return the cached mapping. Throws if LoadAsync hasn’t been called yet.</summary>
        WorkflowMapping Get();
    }
}
