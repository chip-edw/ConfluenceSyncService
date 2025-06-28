using Serilog;

namespace ConfluenceSyncService
{
    public class LoggingHandler : DelegatingHandler
    {
        private readonly IConfiguration _configuration;
        private readonly Serilog.ILogger _logger;

        public LoggingHandler(HttpMessageHandler innerHandler, IConfiguration configuration) : base(innerHandler)
        {
            _configuration = configuration;
            _logger = Log.ForContext<LoggingHandler>();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var enableHTTPLogging = _configuration.GetValue<bool>("LoggingSettings:EnableHTTPLogging");

            if (enableHTTPLogging)
            {
                Log.ForContext("SourceContext", "AutoTaskTicketManager_Base.HTTP")
                    .Debug($"[HTTP Request] {request.Method} {request.RequestUri}");

                if (request.Content != null)
                {
                    string requestContent = await request.Content.ReadAsStringAsync();
                    Log.ForContext("SourceContext", "AutoTaskTicketManager_Base.HTTP")
                        .Debug($"[Request Body] {requestContent}");
                }
            }

            var response = await base.SendAsync(request, cancellationToken);

            if (enableHTTPLogging)
            {
                Log.ForContext("SourceContext", "AutoTaskTicketManager_Base.HTTP")
                    .Debug($"[HTTP Response] {response.StatusCode}");

                if (response.Content != null)
                {
                    string responseContent = await response.Content.ReadAsStringAsync();
                    Log.ForContext("SourceContext", "AutoTaskTicketManager_Base.HTTP")
                        .Debug($"[Response Body] {responseContent}");
                }
            }

            return response;
        }
    }

}
