using Microsoft.Identity.Client;


namespace ConfluenceSyncService.MSGraphAPI
{
    public class MsalHttpClientFactory : IMsalHttpClientFactory
    {
        private readonly IConfiguration _configuration;

        // Constructor to inject IConfiguration
        public MsalHttpClientFactory(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public HttpClient GetHttpClient()
        {
            // Create and configure a custom HTTP handler
            var handler = new LoggingHandler(new HttpClientHandler(), _configuration);

            // Create and return the HttpClient instance with the custom handler
            return new HttpClient(handler);
        }
    }
}
