using Asp.Versioning;
using ConfluenceSyncService.Common.Secrets;
using ConfluenceSyncService.Endpoints; // AckActionHandler type lives here (endpoint handler)
using ConfluenceSyncService.Extensions;
using ConfluenceSyncService.Links;
using ConfluenceSyncService.Services.State;
using ConfluenceSyncService.Services.Workflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Serilog;

namespace ConfluenceSyncService
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Initialize basic console logging first
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .CreateLogger();

            Log.Information("Application starting...");

            AttachGlobalHandlers();

            try
            {
                Log.Information("Creating WebApplication builder...");

                // Build the web app with default providers (appsettings, env vars, cmdline).
                var builder = WebApplication.CreateBuilder(args);

                Log.Information("WebApplication builder created successfully");

                // Logging/Serilog wired to the final configuration (env vars win over JSON).
                builder.Logging.ClearProviders();
                builder.Logging.AddConsole();
                builder.Host.UseSerilog((ctx, services, lc) =>
                    lc.ReadFrom.Configuration(ctx.Configuration));

                Log.Information("Serilog configured");

                // ---- Minimal Linux/App Service hardening ------------------------------------
                var isLinux = OperatingSystem.IsLinux();
                if (isLinux)
                {
                    var cursorPath = builder.Configuration["CursorStore:Path"];
                    if (string.IsNullOrWhiteSpace(cursorPath) || cursorPath.Contains("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase))
                    {
                        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["CursorStore:Path"] = "/home/site/data/cursors.json"
                        });
                    }

                    try
                    {
                        Directory.CreateDirectory("/home/site/data");
                    }
                    catch
                    {
                        // best-effort; ignore if no permission (should succeed on App Service)
                    }
                }
                // ---------------------------------------------------------------------------

                Log.Information("Platform-specific configuration completed");

                // Read ack-only switch AFTER config precedence is correct (env > JSON).
                var ackOnly = builder.Configuration.GetValue<bool>("Hosting:AckOnly");
                Log.Information("AckOnly mode: {AckOnly}", ackOnly);

                // Order matters
                Log.Information("Adding app secrets...");
                builder.Services.AddAppSecrets(builder.Configuration);
                Log.Information("App secrets added successfully");

                // Ensure EF uses the same DB as State:DbPath when no DefaultConnection is set
                var cs = builder.Configuration.GetConnectionString("DefaultConnection");
                if (string.IsNullOrWhiteSpace(cs))
                {
                    var p = builder.Configuration["State:DbPath"] ?? "DB/ConfluenceSyncServiceDB.db";
                    if (!Path.IsPathRooted(p)) p = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, p.Replace('/', Path.DirectorySeparatorChar)));
                    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = $"Data Source={p};Cache=Shared" });
                }
                Log.Information("Database connection string configured");

                Log.Information("Adding app services...");
                builder.Services.AddAppServices(builder.Configuration); // This may register hosted services.
                Log.Information("App services added successfully");

                // In ACK-only mode, strip project-hosted background services so no sync runs.
                if (ackOnly)
                {
                    // Remove IHostedService registrations that belong to this project namespace.
                    var hosted = builder.Services
                        .Where(d =>
                            d.ServiceType == typeof(IHostedService) &&
                            d.ImplementationType != null &&
                            d.ImplementationType.Namespace != null &&
                            d.ImplementationType.Namespace.StartsWith("ConfluenceSyncService", StringComparison.Ordinal))
                        .ToList();

                    foreach (var d in hosted)
                        builder.Services.Remove(d);

                    Log.Information("Hosting:AckOnly=true → removed {count} background hosted services.", hosted.Count);
                }

                Log.Information("Adding controllers and API versioning...");
                builder.Services.AddControllers();
                builder.Services.AddApiVersioning(options =>
                {
                    options.DefaultApiVersion = new ApiVersion(1, 0);
                    options.AssumeDefaultVersionWhenUnspecified = true;
                    options.ReportApiVersions = true;
                    options.ApiVersionReader = ApiVersionReader.Combine(
                        new UrlSegmentApiVersionReader(),
                        new HeaderApiVersionReader("X-Api-Version"),
                        new QueryStringApiVersionReader("api-version"));
                });

                Log.Information("Configuring additional services...");
                ConfigureServices(builder.Services, builder.Configuration);

                // Only configure Kestrel if ASPNETCORE_URLS isn't already provided (avoids override warning on App Service).
                var aspnetUrls = builder.Configuration["ASPNETCORE_URLS"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
                int managementApiPort = builder.Configuration.GetValue<int>("ManagementApiPort", 60020);
                if (string.IsNullOrWhiteSpace(aspnetUrls))
                {
                    builder.WebHost.ConfigureKestrel(options =>
                    {
                        options.ListenAnyIP(managementApiPort);
                    });
                }

                Log.Information("Building application...");
                var app = builder.Build();
                Log.Information("Application built successfully");

                // === One-time SQLite DB self-seed to persistent storage =====================
                try
                {
                    Log.Information("Starting SQLite DB self-seed...");
                    var liveDbPath = app.Configuration["State:DbPath"]; // expects /home/site/data/ConfluenceSyncService/ConfluenceSyncServiceDB.db on App Service
                    if (string.IsNullOrWhiteSpace(liveDbPath))
                    {
                        Log.Warning("State:DbPath is not configured; skipping SQLite DB seed.");
                    }
                    else
                    {
                        var liveDir = Path.GetDirectoryName(liveDbPath);
                        if (!File.Exists(liveDbPath))
                        {
                            // Seed from the packaged DB under content root: /home/site/wwwroot/DB/ConfluenceSyncServiceDB.db
                            var seedPath = Path.Combine(app.Environment.ContentRootPath, "DB", "ConfluenceSyncServiceDB.db");

                            if (!string.IsNullOrEmpty(liveDir))
                            {
                                Directory.CreateDirectory(liveDir);
                            }

                            if (File.Exists(seedPath))
                            {
                                File.Copy(seedPath, liveDbPath, overwrite: false);
                                Log.Information("Seeded SQLite DB to {LiveDbPath} from {SeedPath}", liveDbPath, seedPath);
                            }
                            else
                            {
                                Log.Warning("Seed SQLite DB skipped: packaged seed not found at {SeedPath}. Live path: {LiveDbPath}", seedPath, liveDbPath);
                            }
                        }
                        else
                        {
                            Log.Information("SQLite DB present at {LiveDbPath}; seed skipped.", liveDbPath);
                        }
                    }
                }
                catch (IOException ioex)
                {
                    // If another instance raced the write, treat as success if the file now exists.
                    var liveDbPathNow = app.Configuration["State:DbPath"];
                    if (!string.IsNullOrWhiteSpace(liveDbPathNow) && File.Exists(liveDbPathNow))
                    {
                        Log.Information("SQLite DB already present at {LiveDbPath}; concurrent seed likely occurred. Proceeding.", liveDbPathNow);
                    }
                    else
                    {
                        Log.Error(ioex, "Failed during SQLite DB self-seed.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Unexpected error during SQLite DB self-seed.");
                }
                // ============================================================================

                Log.Information("Starting service initialization...");
                using (var scope = app.Services.CreateScope())
                {
                    Log.Information($"Beginning {nameof(StartupConfiguration)}");

                    var secretsProvider = scope.ServiceProvider.GetRequiredService<ISecretsProvider>();
                    if (secretsProvider is IInitializableSecretsProvider initializableProvider)
                    {
                        await initializableProvider.InitializeAsync();
                    }

                    await StartupConfiguration.LoadProtectedSettingsAsync(secretsProvider);
                    StartupConfiguration.LoadMsGraphConfig();
                    Log.Information($"{nameof(StartupConfiguration.LoadMsGraphConfig)} complete");

                    // Load the workflow mapping once at startup
                    var mappingProvider = scope.ServiceProvider.GetRequiredService<IWorkflowMappingProvider>();
                    await mappingProvider.LoadAsync(); // expects to log workflowId + version

                    var cursorStore = scope.ServiceProvider.GetRequiredService<ICursorStore>();

                    const string TrackerCursorKey = "Cursor:TransitionTracker:lastModifiedUtc";
                    var current = await cursorStore.GetAsync(TrackerCursorKey);
                    if (string.IsNullOrWhiteSpace(current))
                    {
                        // Seed to a safe old date
                        var seed = "2000-01-01T00:00:00Z";
                        await cursorStore.SetAsync(TrackerCursorKey, seed);
                        Log.Information("tracker.cursor seeded {lastModifiedUtc}", seed);
                    }
                    else
                    {
                        Log.Information("tracker.cursor {lastModifiedUtc}", current);
                    }

                    // NOTE: DB migrations intentionally NOT run here in ACK-only mode.
                    // If you later want migrations when running full service, add:
                    // if (!ackOnly) { var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(); db.Database.Migrate(); }
                }

                Log.Information("Configuring endpoints...");
                ConfigureEndpoints(app, managementApiPort);

                Log.Information("Starting application...");
                await app.RunAsync();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "The application failed to start.");
                throw; // Re-throw to ensure non-zero exit code
            }
            finally
            {
                Log.Information("Shutting down...");
                Log.CloseAndFlush();
            }
        }

        private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            Log.Information("Starting ConfigureServices...");

            services.AddLogging();
            services.AddSingleton(configuration);

            var allowedOrigins = configuration.GetSection("AllowedFrontEndOrigins").Get<string[]>();
            services.AddCors(options =>
            {
                options.AddPolicy("AllowFrontend", builder =>
                {
                    builder.WithOrigins(allowedOrigins ?? Array.Empty<string>())
                           .AllowAnyHeader()
                           .AllowAnyMethod();
                });
            });

            Log.Information("ConfigureServices completed");
        }

        private static void ConfigureEndpoints(WebApplication app, int managementApiPort)
        {
            Log.Information("Starting ConfigureEndpoints...");

            app.UseCors("AllowFrontend");
            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });

            // Root endpoint to confirm listening
            app.MapGet("/", (IServiceProvider services) =>
            {
                var server = services.GetRequiredService<IServer>();
                var serverAddresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
                var listeningUrl = serverAddresses?.FirstOrDefault() ?? $"http://localhost:{managementApiPort}";
                Log.Information("Management API is UP and listening at: {listeningUrl}", listeningUrl);

                return $"Management API is UP and listening on: {listeningUrl}";
            });

            // Sanity check endpoint
            app.MapGet("/ping", () => Results.Ok(new { ok = true, t = DateTimeOffset.UtcNow }));

            // HMAC-verified, idempotent action endpoint
            app.MapGet("/maintenance/actions/mark-complete",
                async (HttpContext ctx, AckActionHandler handler, CancellationToken ct) =>
                    await handler.HandleAsync(ctx, ct)
            );

#if DEBUG
            // DEBUG-only helper to mint signed ACK links for testing.
            // Supplies all required params for your ISignedLinkGenerator signature.
            app.MapGet("/maintenance/debug/make-link",
                (int ttlMinutes, string itemId, ISignedLinkGenerator links) =>
                {
                    var nowUtc = DateTime.UtcNow;
                    var targetUtc = nowUtc.AddMinutes(ttlMinutes);

                    string correlationId = itemId;                  // deterministic for tests
                    int regionOffsetMinutes = 0;                    // no regional offset in debug
                    int graceDays = 0;                              // no grace in debug
                    var anchorDateUtc = DateTime.SpecifyKind(nowUtc.Date, DateTimeKind.Utc);
                    int durationBusinessDays = 0;                   // same-day
                    var dueTime = TimeOnly.FromDateTime(targetUtc); // time encodes ttl
                    string? actor = "debug-helper";

                    var url = links.GenerateMarkCompleteLink(
                        itemId,
                        correlationId,
                        regionOffsetMinutes,
                        graceDays,
                        anchorDateUtc,
                        durationBusinessDays,
                        dueTime,
                        actor
                    );

                    return Results.Ok(new { url });
                }
            );
#endif

            Log.Information("ConfigureEndpoints completed");
        }

        private static void AttachGlobalHandlers()
        {
            Log.Information("Attaching global exception handlers...");

            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                Exception? ex = args.ExceptionObject as Exception;
                Log.Fatal(ex, "Unhandled exception: {Message}", ex?.Message);
            };

            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                Log.Fatal(args.Exception, "Unobserved task exception: {Message}", args.Exception?.Message);
                args.SetObserved();
            };

            AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
            {
                Log.Information("Process is exiting. Performing cleanup...");
            };

            Log.Information("Global exception handlers attached");
        }
    }
}
