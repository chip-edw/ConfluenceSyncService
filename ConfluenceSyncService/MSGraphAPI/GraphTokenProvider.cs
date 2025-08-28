using ConfluenceSyncService.Teams;

namespace ConfluenceSyncService.MSGraphAPI;

public sealed class GraphTokenProvider : IGraphTokenProvider
{
    private readonly ConfidentialClientApp _cca;
    public GraphTokenProvider(ConfidentialClientApp cca) => _cca = cca;

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        // Call the method your wrapper already exposes to get an app-only token.
        // Replace with the exact method name you have (e.g., GetAccessToken() / GetAccessTokenAsync()).
        return await _cca.GetAccessToken();
    }
}
