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
                _logger.Debug($"[HTTP Request] {request.Method} {request.RequestUri}");

                if (request.Content != null)
                {
                    string requestContent = await request.Content.ReadAsStringAsync();
                    _logger.Debug($"[Request Body] {requestContent}");
                }
            }

            var response = await base.SendAsync(request, cancellationToken);

            if (enableHTTPLogging)
            {
                _logger.Debug($"[HTTP Response] {response.StatusCode}");

                if (response.Content != null)
                {
                    string responseContent = await response.Content.ReadAsStringAsync();
                    _logger.Debug($"[Response Body] {responseContent}");
                }
            }

            return response;
        }
    }
}