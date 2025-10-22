using ConfluenceSyncService.Interfaces;
using System.Text.Json;

namespace ConfluenceSyncService.Services.Workflow
{
    public sealed class CategoryOrderProvider : ICategoryOrderProvider
    {
        private readonly IConfiguration _config;
        private readonly IHostEnvironment _env;
        private readonly ILogger<CategoryOrderProvider> _log;
        private Dictionary<(string Category, string AnchorDateType), int>? _map;

        private const string ConfigKey = "ChaserJob:WorkflowTemplatePath";

        public CategoryOrderProvider(IConfiguration config, IHostEnvironment env, ILogger<CategoryOrderProvider> log)
        {
            _config = config;
            _env = env;
            _log = log;
        }

        public async ValueTask LoadAsync(CancellationToken ct = default)
        {
            if (_map != null) return;

            var relPath = _config[ConfigKey];
            if (string.IsNullOrWhiteSpace(relPath))
                throw new InvalidOperationException($"Missing config key '{ConfigKey}'.");

            // Resolve relative to content root
            var path = Path.IsPathRooted(relPath) ? relPath : Path.Combine(_env.ContentRootPath, relPath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Workflow template not found at: {path}", path);

            await using var fs = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct);

            // Expect shape: { "WorkflowId": "...", "Activities": [ { "Category": "...", "AnchorDateType": "...", ... }, ... ] }
            if (!doc.RootElement.TryGetProperty("Activities", out var activities) || activities.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Workflow_template.json is missing 'Activities' array.");

            var order = new Dictionary<(string Category, string AnchorDateType), int>();
            var listForLog = new List<string>();

            foreach (var item in activities.EnumerateArray())
            {
                if (!item.TryGetProperty("Category", out var catProp) || catProp.ValueKind != JsonValueKind.String)
                    continue;

                if (!item.TryGetProperty("AnchorDateType", out var anchorProp) || anchorProp.ValueKind != JsonValueKind.String)
                    continue;

                var category = catProp.GetString() ?? "";
                var anchorDateType = anchorProp.GetString() ?? "";

                if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(anchorDateType))
                    continue;

                var key = (category, anchorDateType);
                if (!order.ContainsKey(key))
                {
                    var idx = order.Count;
                    order[key] = idx;
                    listForLog.Add($"[{idx}] \"{category}\" (Anchor: {anchorDateType})");
                }
            }

            if (order.Count == 0)
                throw new InvalidOperationException("No categories discovered in workflow template.");

            _map = order;
            _log.LogInformation("gate.order loaded: {OrderedCategories}", string.Join(", ", listForLog));
        }

        public IReadOnlyDictionary<(string Category, string AnchorDateType), int> GetMap()
            => _map ?? throw new InvalidOperationException("CategoryOrderProvider not loaded. Call LoadAsync() once at startup.");
    }
}
