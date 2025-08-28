using ConfluenceSyncService.Interfaces;
using ConfluenceSyncService.Models;
using ConfluenceSyncService.MSGraphAPI;
using ConfluenceSyncService.Services;
using ConfluenceSyncService.Utilities;
using Serilog;

namespace ConfluenceSyncService
{
    //Used for executing methods within the worker class from the Management API
    public interface IWorkerService
    {
        Task DoWorkAsync(CancellationToken cancellationToken);
        void StopService();
    }

    public class Worker : BackgroundService, IWorkerService
    {
        #region Private readonly and Constructor

        //private readonly DbContextOptions<ApplicationDbContext> _dbOptions;
        private readonly Serilog.ILogger _logger;
        private volatile bool _cancelTokenIssued = false;
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private readonly ConfidentialClientApp _confidentialClientApp;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly StartupLoaderService _startupLoaderService;
        private readonly IConfiguration _configuration;

        private bool _isInitialized = false;

        public Worker(ConfidentialClientApp confidentialClientApp, IConfiguration configuration, ILogger<Worker> logger, IServiceScopeFactory serviceScopeFactory, StartupLoaderService startupLoaderService)
        {
            _confidentialClientApp = confidentialClientApp;
            _logger = Log.ForContext<Worker>();
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _startupLoaderService = startupLoaderService ?? throw new ArgumentNullException(nameof(startupLoaderService));
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        }

        #endregion

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            // DO NOT block here - let Kestrel start first
            _logger.Information("Worker service starting...");

            // Call base to start the background service
            await base.StartAsync(cancellationToken);


            //######################  Various utilities  ##########################

            //using var scope = _serviceScopeFactory.CreateScope();
            //var workerUtilities = new WorkerUtilities(_serviceScopeFactory);

            ////Validation: Get the SharePoint List actual Field Values
            //await workerUtilities.ListSharePointFieldNamesAsync(
            //    "v7n2m.sharepoint.com,d1ee4683-057e-41c1-abe8-8b7fcf24a609,37b9c1e6-3b8e-4e8e-981b-67291632e4c3",
            //    "Phase Tasks & Metadata");
            //Console.WriteLine("");

            ////Validation: Discover Teams resources
            //try
            //{
            //    Console.WriteLine("\n=== TEAMS DISCOVERY UTILITY ===");
            //    _logger.Information("=== STARTING TEAMS DISCOVERY ===");

            //    await workerUtilities.DiscoverTeamsResourcesAsync();

            //    Console.WriteLine("=== END TEAMS DISCOVERY ===\n");
            //}
            //catch (Exception ex)
            //{
            //    _logger.Error(ex, "Failed to discover Teams resources");
            //}

            //Console.WriteLine();

            ////TEST: Create new Transition Tracker table with fixed Region field
            //try
            //{
            //    Console.WriteLine("\n\n");
            //    _logger.Information("=== TESTING NEW TABLE CREATION ===");

            //    var createSuccess = await _confluenceClient.CreateTransitionTrackerTableAsync("6324227", "Customer Wiki Template");
            //    Console.WriteLine($"Table creation successful: {createSuccess}");
            //}
            //catch (Exception ex)
            //{
            //    _logger.Error(ex, "Failed to create new table");
            //}
            //_logger.Information("=== END TABLE CREATION TEST ===");



            //Console.Write("\n\n");
            //// TEST: Update status text based on colors and parse table
            //try
            //{
            //    _logger.Information("=== CONFLUENCE STATUS TEXT UPDATE AND PARSING ===");

            //    // First, update any status text based on current colors
            //    var updateSuccess = await _confluenceClient.UpdateStatusTextBasedOnColorAsync("4554759");
            //    Console.WriteLine($"Status text update successful: {updateSuccess}");

            //    // Then parse the table data
            //    var tableData = await _confluenceClient.ParseTransitionTrackerTableAsync("4554759");

            //    Console.WriteLine("=== PARSED TABLE DATA ===");
            //    foreach (var kvp in tableData)
            //    {
            //        Console.WriteLine($"{kvp.Key}: {kvp.Value}");
            //    }

            //}
            //catch (Exception ex)
            //{
            //    _logger.Error(ex, "Failed to test status update and parsing");
            //}
            //_logger.Information("=== END STATUS UPDATE AND PARSING TEST ===");


        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Add small delay to let Kestrel start up
            await Task.Delay(2000, stoppingToken);

            if (!_isInitialized)
            {
                await InitializeAsync(stoppingToken);
                _isInitialized = true;
            }

            _logger.Information("Entering Main Worker Service ExecuteAsync Method");

            // Get the wait period at the bottom of each loop
            int timeDelay = _configuration.GetValue<int>("GeneralSettings:TimeDelay", 60) * 1000; // Default 60 seconds

            try
            {
                // Initial token acquisition
                await AcquireInitialTokenAsync(stoppingToken);

                // Service Start Admin Email
                var workerUtilities = new WorkerUtilities(_serviceScopeFactory);
                await workerUtilities.SendServiceStartupEmailAsync();

                // Main worker loop
                while (!stoppingToken.IsCancellationRequested && !_cancelTokenIssued)
                {
                    try
                    {
                        await ProcessWorkCycleAsync(stoppingToken);

                        // Wait for the configured delay or until cancellation
                        await Task.Delay(timeDelay, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Information("Operation canceled. Exiting loop.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Unexpected error in background loop.");
                        // Wait a bit before retrying to avoid tight error loops
                        await Task.Delay(5000, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Information("Worker service execution was canceled.");
            }
            catch (Exception ex)
            {
                _logger.Fatal(ex, "Fatal error in worker service execution.");
                throw; // Re-throw fatal errors
            }

            _logger.Information("Exiting ExecuteAsync loop.");
        }

        private async Task InitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.Information("Preparing to start Confluence Sync Service");
                _logger.Information("Waiting 5 sec to ensure all Network Dependencies available.");

                // Use Task.Delay instead of Thread.Sleep to be non-blocking
                await Task.Delay(5000, cancellationToken);

                // Initialize startup configuration
                await _startupLoaderService.LoadAllStartupDataAsync();

                // Create service scope for initialization
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Get Assembly Version
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var assemblyVersion = assembly.GetName().Version;

                // Log startup banner
                await Task.Delay(500, cancellationToken); // Small delay for clean logging

                _logger.Information("");
                _logger.Information(" ____________________________________________");
                _logger.Information("|                                            |");
                _logger.Information("|          Confluence Sync Service           |");
                _logger.Information("|                                            |");
                _logger.Information($"|        Application version: {assemblyVersion}        |");
                _logger.Information("|____________________________________________|");
                _logger.Information("");

                _logger.Information("Initialization complete.");
            }
            catch (Exception ex)
            {
                _logger.Fatal(ex, "Failed to initialize worker service");
                throw;
            }
        }

        private async Task AcquireInitialTokenAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.Information(">>> About to get MSGraph access token...");

                string accessToken = await _confidentialClientApp.GetAccessToken();
                _logger.Information(">>> Token acquired: {FirstTen}", accessToken.Substring(0, 10));

                //// Optional: Send startup email (configure via appsettings)
                //bool sendStartupEmail = _configuration.GetValue<bool>("GeneralSettings:SendStartupEmail", false);
                //if (sendStartupEmail)
                //{
                //    await SendStartupNotificationAsync(accessToken);
                //}
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to retrieve the MSAL Bearer Token");
                // Don't throw - let the worker continue and retry
            }
        }

        //private async Task SendStartupNotificationAsync(string accessToken)
        //{
        //    try
        //    {
        //        string subject = $"Confluence Sync Service - Worker Started Successfully - {DateTime.Now}";
        //        string body = $"The Confluence Sync Service Worker has started at {DateTime.Now}";
        //        string adminEmail = _configuration.GetValue<string>("GeneralSettings:AdminEmail", "admin@example.com");
        //        string url = "https://graph.microsoft.com/v1.0/users/attms@v7n2m.onmicrosoft.com/sendMail";

        //        // Implement your email sending logic here
        //        // await _emailApiHelper.SendEmailAsync(url, accessToken, subject, body, adminEmail);

        //        _logger.Information("Startup notification email sent to {AdminEmail}", adminEmail);
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.Warning(ex, "Failed to send startup notification email");
        //        // Don't throw - this is not critical
        //    }
        //}

        private async Task ProcessWorkCycleAsync(CancellationToken cancellationToken)
        {
            bool msGraphTokenValid = Authenticate.GetExpiresOn() > DateTime.UtcNow;

            if (msGraphTokenValid)
            {
                _logger.Debug("MS Graph tokens valid at: {time}", DateTimeOffset.Now);

                // Create scope to access scoped services
                using var scope = _serviceScopeFactory.CreateScope();
                var syncOrchestratorService = scope.ServiceProvider.GetRequiredService<ISyncOrchestratorService>();

                _logger.Debug("=== STARTING TABLE SYNC CYCLE ===");
                try
                {
                    await syncOrchestratorService.RunSyncAsync(cancellationToken);
                    _logger.Debug("=== TABLE SYNC CYCLE COMPLETED ===");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "=== TABLE SYNC CYCLE FAILED ===");
                }
            }
            else
            {
                _logger.Information("Token refresh needed: MSGraph Valid = {MS}", msGraphTokenValid);

                try
                {
                    _logger.Information("Refreshing MS Graph token...");
                    await _confidentialClientApp.GetAccessToken();
                    _logger.Debug("Acquired new MS Graph token.");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to refresh MS Graph token");
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Method for executing work from the Management API
        /// </summary>
        public async Task DoWorkAsync(CancellationToken cancellationToken)
        {
            _logger.Information($"DoWorkAsync starting. CancellationRequested: {cancellationToken.IsCancellationRequested}");

            if (!_isInitialized)
            {
                await InitializeAsync(cancellationToken);
                _isInitialized = true;
            }

            await ProcessWorkCycleAsync(cancellationToken);
        }

        public void StopService()
        {
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                _logger.Information("Internal Stop service Cancellation Token Received...");
                try
                {
                    _cancellationTokenSource.Cancel();
                    _cancelTokenIssued = true;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Error while canceling service");
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.Information("Worker service stopping...");
            StopService();
            await base.StopAsync(cancellationToken);
        }

    }
}
