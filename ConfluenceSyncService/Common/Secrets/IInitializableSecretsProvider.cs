namespace ConfluenceSyncService.Common.Secrets
{
    public interface IInitializableSecretsProvider
    {
        Task InitializeAsync();
    }
}
