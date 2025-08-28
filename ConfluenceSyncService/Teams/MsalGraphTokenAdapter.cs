using ConfluenceSyncService.MSGraphAPI; // your existing wrapper

namespace ConfluenceSyncService.Teams
{
    /// <summary>
    /// Adapter that exposes your existing ConfidentialClientApp via IGraphTokenProvider.
    /// </summary>
    public sealed class MsalGraphTokenAdapter : IGraphTokenProvider
    {
        private readonly ConfidentialClientApp _cca;
        public MsalGraphTokenAdapter(ConfidentialClientApp cca) => _cca = cca;

        public Task<string> GetTokenAsync(CancellationToken ct)
            => _cca.GetAccessToken(); // uses MSAL cache; no new token system introduced
    }
}
