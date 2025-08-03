using Asp.Versioning;
using ConfluenceSyncService.Common.Secrets;
using ConfluenceSyncService.Extensions;
using ConfluenceSyncService.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Identity.Client;
using Serilog;

namespace ConfluenceSyncService
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Build configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            // Configure Serilog
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();

            // Attach global handlers for error handling
            AttachGlobalHandlers();

            try
            {
                // Create the WebApplication builder
                var builder = WebApplication.CreateBuilder(args);

                // Explicitly configure Serilog for the WebApplication builder
                builder.Logging.ClearProviders();
                builder.Logging.AddConsole();
                builder.Host.UseSerilog();

                // Add the configuration to the builder
                builder.Configuration.AddConfiguration(configuration);

                // Register Services
                ConfigureServices(builder.Services, builder.Configuration);

                // Add Controllers (required for MapControllers to work)
                builder.Services.AddControllers();

                // API Versioning
                builder.Services.AddApiVersioning(options =>
                {
                    options.DefaultApiVersion = new ApiVersion(1, 0);
                    options.AssumeDefaultVersionWhenUnspecified = true;
                    options.ReportApiVersions = true;
                    options.ApiVersionReader = ApiVersionReader.Combine(
                        new UrlSegmentApiVersionReader(),
                        new HeaderApiVersionReader("X-Api-Version"),
                        new QueryStringApiVersionReader("api-version")
                    );
                });

                // Determine secrets provider type
                string secretsProviderType = configuration["SecretsProvider:Type"] ?? "Sqlite";

                // Only do early loading for Azure Key Vault (to cache secrets)
                if (secretsProviderType == "AzureKeyVault")
                {
                    // Create a temporary service provider for Azure Key Vault only
                    var tempServices = new ServiceCollection();
                    tempServices.AddSingleton<IConfiguration>(configuration);
                    tempServices.AddAppSecrets(configuration);
                    using var tempProvider = tempServices.BuildServiceProvider();
                    var prebuildSecretsProvider = tempProvider.GetRequiredService<ISecretsProvider>();

                    // Initialize the provider to cache secrets
                    if (prebuildSecretsProvider is IInitializableSecretsProvider initializableProvider)
                    {
                        await initializableProvider.InitializeAsync();
                    }

                    await StartupConfiguration.LoadProtectedSettingsAsync(prebuildSecretsProvider);
                }

                // Register all app services (includes DbContext for SQLite case)
                builder.Services.AddAppServices();
                builder.Services.AddAppSecrets(configuration);

                // Configure Kestrel for the internal maintenance API
                int managementApiPort = configuration.GetValue<int>("ManagementApiPort");
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.ListenAnyIP(managementApiPort);
                });

                // Build and run the application
                var app = builder.Build();

                // Handle secrets loading after app is built
                using (var scope = app.Services.CreateScope())
                {
                    Log.Information($"Beginning {nameof(StartupConfiguration)}\n");

                    var secretsProvider = scope.ServiceProvider.GetRequiredService<ISecretsProvider>();

                    // Initialize SQLite secrets provider if needed
                    if (secretsProvider is IInitializableSecretsProvider initializableProvider)
                    {
                        await initializableProvider.InitializeAsync();
                    }

                    // Load protected settings (this might be redundant for AzureKeyVault case, but safe)
                    await StartupConfiguration.LoadProtectedSettingsAsync(secretsProvider);

                    // Load MS Graph config
                    StartupConfiguration.LoadMsGraphConfig();
                    Log.Information($"{nameof(StartupConfiguration.LoadMsGraphConfig)}");

                    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                    var httpClientFactory = scope.ServiceProvider.GetRequiredService<IMsalHttpClientFactory>();
                }

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
            services.AddScoped<IWorkerService, Worker>();
            services.AddHostedService<ScopedWorkerHostedService>();

            // Load allowed origins from config
            var allowedOrigins = configuration.GetSection("AllowedFrontEndOrigins").Get<string[]>();

            services.AddCors(options =>
            {
                options.AddPolicy("AllowFrontend", builder =>
                {
                    builder.WithOrigins(allowedOrigins!)
                           .AllowAnyHeader()
                           .AllowAnyMethod();
                });
            });
        }


        private static void AttachGlobalHandlers()
        {
            // Handle Unhandled Exceptions
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                Exception ex = args.ExceptionObject as Exception;
                Log.Fatal($"Unhandled exception: {ex?.Message}", ex);
            };

            // Handle Task Unobserved Exceptions
            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                Log.Fatal($"Unobserved task exception: {args.Exception?.Message}", args.Exception);
                args.SetObserved(); // Prevents the process from being terminated
            };

            // Handle Process Exit
            AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
            {
                Log.Information("Process is exiting. Performing cleanup...");
            };
        }
    }
}