using System.Text.Json;

namespace ConfluenceSyncService.Services.Workflow
{
    public sealed class WorkflowMappingProvider : IWorkflowMappingProvider
    {
        private readonly IConfiguration _config;
        private readonly IHostEnvironment _env;
        private readonly ILogger<WorkflowMappingProvider> _logger;
        private WorkflowMapping? _cache;
        private readonly object _gate = new();

        public WorkflowMappingProvider(
            IConfiguration config,
            IHostEnvironment env,
            ILogger<WorkflowMappingProvider> logger)
        {
            _config = config;
            _env = env;
            _logger = logger;
        }

        public ValueTask LoadAsync(CancellationToken ct = default)
        {
            if (_cache != null) return ValueTask.CompletedTask;

            lock (_gate)
            {
                if (_cache != null) return ValueTask.CompletedTask;

                var relPath = _config["WorkflowMapping:Path"];
                if (string.IsNullOrWhiteSpace(relPath))
                    throw new InvalidOperationException("Missing config: WorkflowMapping:Path");

                var fullPath = Path.IsPathRooted(relPath)
                    ? relPath
                    : Path.Combine(_env.ContentRootPath, relPath);

                if (!File.Exists(fullPath))
                    throw new FileNotFoundException($"Workflow mapping file not found: {fullPath}");

                var json = File.ReadAllText(fullPath);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                using var doc = JsonDocument.Parse(json);
                var mapped = JsonSerializer.Deserialize<WorkflowMapping>(json, opts)
                             ?? throw new InvalidOperationException("Failed to deserialize workflow mapping.");
                mapped.Raw = doc.RootElement.Clone();

                _cache = mapped;

                _logger.LogInformation(
                    "Workflow mapping loaded (workflowId={WorkflowId}, version={Version}, path={Path})",
                    _cache.WorkflowId, _cache.Version, relPath);
            }

            return ValueTask.CompletedTask;
        }

        public WorkflowMapping Get()
            => _cache ?? throw new InvalidOperationException("Workflow mapping not loaded. Call LoadAsync() during startup.");
    }
}
