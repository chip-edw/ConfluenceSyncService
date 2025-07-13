using Asp.Versioning;
using ConfluenceSyncService.Extensions;
using ConfluenceSyncService.Models;
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
                builder.Logging.ClearProviders(); // Clear default logging providers
                builder.Logging.AddConsole();    // Add console logging back
                builder.Host.UseSerilog();       // Use Serilog as the primary logging provider


                // Add the configuration to the builder
                builder.Configuration.AddConfiguration(configuration);

                // Register Services
                ConfigureServices(builder.Services, builder.Configuration);

                // Add Controllers (required for MapControllers to work)
                builder.Services.AddControllers();

                //Went for the more robust versioning for the maintenance API
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

                // Begin Register Services and Singletons - See Extensions.ServiceCollectionExtensions.cs 
                builder.Services.AddAppServices();


                // Configure Kestrel for the internal maintenance API
                int managementApiPort = configuration.GetValue<int>("ManagementApiPort");
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.ListenAnyIP(managementApiPort); // Bind to the specified port
                });

                // Build and run the application
                var app = builder.Build();

                //Enable plugins to resolve scoped services
                //PluginContracts.ServiceActivator.ServiceProvider = app.Services;


                // Create a scope to access scoped services
                using (var scope = app.Services.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    Log.Information($"Beginning {nameof(StartupConfiguration)}\n");

                    // Load settings using the scoped dbContext
                    StartupConfiguration.LoadProtectedSettings(dbContext);
                    Log.Information($"{nameof(StartupConfiguration.LoadProtectedSettings)}");


                    //Loads the necessary values for the MS Graph API. Includes values nessary to retrieve the Bearer Access Token
                    //from the Azure Authentication Service
                    //Must be loaded before initializing the EmailManager as these settings are involved in the MSGraph authentication
                    StartupConfiguration.LoadMsGraphConfig();
                    Log.Information($"{nameof(StartupConfiguration.LoadMsGraphConfig)}");

                    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                    var httpClientFactory = scope.ServiceProvider.GetRequiredService<IMsalHttpClientFactory>();

                }

                // Map the maintenance API endpoints
                //ConfigureEndpoints(app, managementApiPort);

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