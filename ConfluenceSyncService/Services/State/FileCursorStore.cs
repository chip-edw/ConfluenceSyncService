using System.Text.Json;

namespace ConfluenceSyncService.Services.State
{
    public sealed class FileCursorStore : ICursorStore
    {
        private readonly string _fullPath;
        private readonly ILogger<FileCursorStore> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public FileCursorStore(IConfiguration config, IHostEnvironment env, ILogger<FileCursorStore> logger)
        {
            _logger = logger;

            var configured = config["State:CursorsPath"] ?? "Data/State/cursors.json";

            // Expand environment variables like %LOCALAPPDATA%
            var expanded = Environment.ExpandEnvironmentVariables(configured);

            // Optional: support "~/" for cross-platform dev
            if (!Path.IsPathRooted(expanded) && expanded.StartsWith("~/", StringComparison.Ordinal))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                expanded = Path.Combine(home, expanded.Substring(2));
            }

            // Anchor relative paths to the app's content root
            _fullPath = Path.IsPathRooted(expanded)
                ? expanded
                : Path.Combine(env.ContentRootPath, expanded);

            var dir = Path.GetDirectoryName(_fullPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (!File.Exists(_fullPath)) File.WriteAllText(_fullPath, "{}");

            // Helpful startup log
            _logger.LogInformation("cursor.store.path={CursorPath}", _fullPath);
        }


        public async Task<string?> GetAsync(string key, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                using var fs = File.OpenRead(_fullPath);
                var dict = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(fs, cancellationToken: ct)
                           ?? new Dictionary<string, string>();
                return dict.TryGetValue(key, out var val) ? val : null;
            }
            finally { _gate.Release(); }
        }

        public async Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                Dictionary<string, string> dict;
                using (var fs = File.OpenRead(_fullPath))
                {
                    dict = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(fs, cancellationToken: ct)
                           ?? new Dictionary<string, string>();
                }
                dict[key] = value;

                using var outFs = File.Create(_fullPath);
                await JsonSerializer.SerializeAsync(outFs, dict, cancellationToken: ct);
                _logger.LogInformation("Cursor set {Key}={Value}", key, value);
            }
            finally { _gate.Release(); }
        }
    }
}
