using Asp.Versioning;
using ConfluenceSyncService.Common.Secrets;
using ConfluenceSyncService.Endpoints; // AckActionHandler type lives here (endpoint handler)
using ConfluenceSyncService.Extensions;
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
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();

            AttachGlobalHandlers();

            try
            {
                var builder = WebApplication.CreateBuilder(args);

                builder.Logging.ClearProviders();
                builder.Logging.AddConsole();
                builder.Host.UseSerilog();

                builder.Configuration.AddConfiguration(configuration);

                // Order matters
                builder.Services.AddAppSecrets(builder.Configuration);
                builder.Services.AddAppServices(builder.Configuration);

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

                ConfigureServices(builder.Services, builder.Configuration);

                int managementApiPort = builder.Configuration.GetValue<int>("ManagementApiPort", 60020);
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.ListenAnyIP(managementApiPort);
                });

                var app = builder.Build();

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

                    //Load the workflow mapping once at startup
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

                }

                ConfigureEndpoints(app, managementApiPort);

                await app.RunAsync();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "The application failed to start.");
            }
            finally
            {
                Log.Information("Shutting down...");
                Log.CloseAndFlush();
            }
        }

        private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
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
        }

        private static void ConfigureEndpoints(WebApplication app, int managementApiPort)
        {
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

            // NEW: GET /maintenance/actions/mark-complete (HMAC-verified, idempotent)
            app.MapGet("/maintenance/actions/mark-complete",
                async (HttpContext ctx, AckActionHandler handler, CancellationToken ct)
                    => await handler.HandleAsync(ctx, ct));
        }

        private static void AttachGlobalHandlers()
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                Exception ex = args.ExceptionObject as Exception;
                Log.Fatal($"Unhandled exception: {ex?.Message}", ex);
            };

            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                Log.Fatal($"Unobserved task exception: {args.Exception?.Message}", args.Exception);
                args.SetObserved();
            };

            AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
            {
                Log.Information("Process is exiting. Performing cleanup...");
            };
        }
    }
}
