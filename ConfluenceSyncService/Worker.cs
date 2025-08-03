
using ConfluenceSyncService.Interfaces;
using ConfluenceSyncService.Models;
using ConfluenceSyncService.MSGraphAPI;
using ConfluenceSyncService.Services;
using ConfluenceSyncService.Services.Clients;
using Microsoft.EntityFrameworkCore;
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

        private readonly DbContextOptions<ApplicationDbContext> _dbOptions;

        private readonly Serilog.ILogger _logger;
        private volatile bool cancelTokenIssued = false;

        //private CancellationTokenSource _cancellationTokenSource;
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private readonly ConfidentialClientApp _confidentialClientApp;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly StartupLoaderService _startupLoaderService;
        private readonly IConfiguration _configuration;
        private readonly ConfluenceClient _confluenceClient;
        private readonly ISyncOrchestratorService _syncOrchestratorService;
        private readonly SharePointClient _sharePointClient;



        public Worker(ConfidentialClientApp confidentialClientApp, IConfiguration configuration, ILogger<Worker> logger, IServiceScopeFactory serviceScopeFactory,
            StartupLoaderService startupLoaderService, DbContextOptions<ApplicationDbContext> dbOptions,
            ConfluenceClient confluenceClient, ISyncOrchestratorService syncOrchestratorService, SharePointClient sharePointClient)
        {
            _confidentialClientApp = confidentialClientApp;
            _logger = Log.ForContext<Worker>();
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _startupLoaderService = startupLoaderService ?? throw new ArgumentNullException(nameof(startupLoaderService));
            _dbOptions = dbOptions;
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
            _confluenceClient = confluenceClient;
            _syncOrchestratorService = syncOrchestratorService ?? throw new ArgumentException(nameof(syncOrchestratorService));
            _sharePointClient = sharePointClient;
        }

        #endregion


        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            // initialize the dbOptions for the Management API.
            //ManagementApiHelper.Initialize(_dbOptions);


            //Get OS 
            var os = StartupConfiguration.DetermineOS();


            // ##########          Begin Application Startup and Prechecks          ##########

            _logger.Information("Preparing to start Confluence Sync Service \n " +
                "Waiting 5 sec to ensure all Network Dependancies available. \n\n");
            Thread.Sleep(5 * 1000);

            //####################### WE INITIALIZE THE STARTUP CONFIGURATION HERE ##########################
            // ################ Loading Configuration.

            await _startupLoaderService.LoadAllStartupDataAsync();

            ////Validation: Get the SharePoint List actual Field Values
            //try
            //{
            //    Console.WriteLine("\n\n");
            //    _logger.Information("=== DISCOVERING SHAREPOINT FIELD NAMES ===");

            //    var fieldMap = await _sharePointClient.GetListFieldsAsync(
            //        "v7n2m.sharepoint.com,d1ee4683-057e-41c1-abe8-8b7fcf24a609,37b9c1e6-3b8e-4e8e-981b-67291632e4c3",
            //        "Transition Tracker");

            //    Console.WriteLine("SharePoint Field Mappings:");
            //    foreach (var field in fieldMap)
            //    {
            //        Console.WriteLine($"Display: '{field.Key}' -> Internal: '{field.Value}'");
            //    }
            //}
            //catch (Exception ex)
            //{
            //    _logger.Error(ex, "Failed to get SharePoint field names");
            //}
            //_logger.Information("=== END SHAREPOINT FIELD NAMES DISCOVERY ===");


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

            //########################################################################################


            //####################### WE INITIALIZE THE LOADED PLUGINS HERE ##########################
            // ################ Loading Plugins. Need to load before the scope is created
            // _pluginManager.LoadPlugins();

            //########################################################################################


            // Architecture Note:
            // =============================================================
            // We create a new IServiceScope for each scheduled job execution
            // because Scheduler Plugins spin up on independent background threads.
            // 
            // Entity Framework Core DbContext is NOT thread-safe, and each thread
            // must use its own scoped DbContext instance.
            // 
            // This ensures that concurrent jobs do not share DbContext instances
            // and remain thread-safe and isolated.
            //
            // DO NOT attempt to share a single DbContext or DbContextOptions across
            // scheduler jobs without a new scope per execution.
            //
            // Reference: EF Core Thread Safety - https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/
            // =============================================================



            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();


                #region This is placeholder code for the future Plugin Framework and Scheduler
                // Load scheduled jobs safely through scoped loader
                //var jobLoader = scope.ServiceProvider.GetRequiredService<ISchedulerJobLoader>();
                //var jobs = await jobLoader.LoadJobsAsync(cancellationToken);

                // var resultReporter = scope.ServiceProvider.GetRequiredService<ISchedulerResultReporter>();
                // var jobInstances = _pluginManager.Jobs;

                //foreach (var plugin in _pluginManager.Plugins.OfType<ISchedulerPlugin>())
                //{
                //    plugin.SetJobs(jobs);
                //    plugin.SetResultReporter(resultReporter);
                //    plugin.Start();  // This spins up the scheduler thread
                //}
                #endregion


            }

            //Get Assembly Version
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var assemblyVersion = assembly.GetName().Version;


            // ##########          Completes Application Startup and Prechecks          ##########

            // Need to pause and let scheduler threads and configuration catch up so we dont step on the following Log with the BOX w/ Version
            // It is only affecting the console logging but I like a pretty picture

            Thread.Sleep(1 * 500);
            Log.Information("\n\n");


            _logger.Information(" ____________________________________________");
            _logger.Information("|                                            |");
            _logger.Information("|          Confluence Sync Service           |");
            _logger.Information("|                                            |");
            _logger.Information($"|        Application version: {assemblyVersion}        |");
            _logger.Information("|____________________________________________|");
            _logger.Information("\r\n\r\n");

        }




        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            _logger.Information("Entering Main Worker Service ExecuteAsync Method\n");

            //Get the wait period at the bottom of each loop
            int timeDelay = _configuration.GetValue<int>("GeneralSettings:TimeDelay") * 1000;

            _logger.Information(">>> About to get MSGraph access token...");

            Task<string> tokenTask = _confidentialClientApp.GetAccessToken();

            _logger.Information(">>> Task for token created. Status: {Status}", tokenTask.Status);


            try
            {
                string accessToken = await _confidentialClientApp.GetAccessToken();
                _logger.Information(">>> Token acquired: {FirstTen}", accessToken.Substring(0, 10));

                //Send a test e-mail message using the refactored ProtectedApiCallHelper Class

                string subject = $"Confluence Sync Service - Worker Started Successfully - {System.DateTime.Now}";
                string body = $"The Confluence Sync Service Worker has started at {System.DateTime.Now}";
                string adminEmail = "chip.edw@gmail.com";
                string url = "https://graph.microsoft.com/v1.0/users/attms@v7n2m.onmicrosoft.com/sendMail";

                //Comment or uncomment following line based on if you want an e-mail sent in startup
                //Later put config in Appsettings.json to control this

                //await _emailApiHelper.SendEmailAsync(url, accessToken, subject, body, adminEmail);

            }
            catch (Exception ex)
            {
                _logger.Error("Failed to retrieve the MSAL Bearer Token. Error: {ErrorMessage}", ex.Message);

                _logger.Error("Graph API call correlation ID: {CorrelationId}");
            }



            while (!cancellationToken.IsCancellationRequested && !cancelTokenIssued)
            {
                //_logger.Debug("Worker heartbeat at: {time}", DateTimeOffset.Now);

                try
                {
                    bool msGraphTokenValid = Authenticate.GetExpiresOn() > DateTime.UtcNow;

                    if (msGraphTokenValid)
                    {
                        // Just to gove some indication on the console that the loop is still running
                        _logger.Debug("MS Graph tokens valid at: {time}", DateTimeOffset.Now);

                        #region Plugin Loader Place Holder
                        #endregion

                        #region Invoke SyncOrchestratorService

                        _logger.Debug("=== STARTING TABLE SYNC CYCLE ===");
                        try
                        {
                            // Run sync here
                            await _syncOrchestratorService.RunSyncAsync(cancellationToken);
                            _logger.Debug("=== TABLE SYNC CYCLE COMPLETED ===");
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "=== TABLE SYNC CYCLE FAILED ===");
                            // Don't re-throw - let the worker continue
                        }

                        #endregion

                        await Task.Delay(timeDelay, cancellationToken); //Pausing for seconds set in SQL DB ConfigStore Table under TimeDelay

                    }
                    else
                    {
                        _logger.Information("Token refresh needed: MSGraph Valid = {MS}", msGraphTokenValid);

                        if (!msGraphTokenValid)
                        {
                            _logger.Information("Refreshing MS Graph token...");
                            await _confidentialClientApp.GetAccessToken();
                            _logger.Debug("Acquired new MS Graph token.");
                        }


                        await Task.Delay(1000, cancellationToken); // Short delay before next loop
                    }

                }

                catch (OperationCanceledException)
                {
                    _logger.Information("Operation canceled. Exiting loop.");
                    break; // Exit the loop on cancellation
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Unexpected error in background loop.");
                }


                await Task.Delay(1000, cancellationToken);
            }

            _logger.Information("Exiting ExecuteAsync loop.");

        }

        /// <summary>
        /// Holds the Logic for calling methods or performing background work. Currently just being used to help manage the cancellatioin Token.
        /// It will look every 1 second for a cancellationToken to be issued.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task DoWorkAsync(CancellationToken cancellationToken)
        {
            Log.Information($"DoWorkAsync starting. CancellationRequested: {cancellationToken.IsCancellationRequested}");

            await ExecuteAsync(cancellationToken);// This is the main background loop of the app that is the core

        }

        public async void StopService()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _logger.Information("Internal Stop service Cancellation Token Received ...");
                try
                {
                    await _cancellationTokenSource.CancelAsync();
                }
                catch (OperationCanceledException)
                {
                    _logger.Warning("Operation was already canceled.");
                }
                finally
                {
                    _cancellationTokenSource.Cancel();
                }
            }
            cancelTokenIssued = true;
        }



    }
}
