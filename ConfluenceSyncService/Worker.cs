using ConfluenceSyncService.Models;
using ConfluenceSyncService.MSGraphAPI;
using ConfluenceSyncService.Services;
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


        public Worker(ConfidentialClientApp confidentialClientApp, IConfiguration configuration, ILogger<Worker> logger, IServiceScopeFactory serviceScopeFactory,
            StartupLoaderService startupLoaderService, DbContextOptions<ApplicationDbContext> dbOptions)
        {
            _confidentialClientApp = confidentialClientApp;
            _logger = Log.ForContext<Worker>();
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _startupLoaderService = startupLoaderService ?? throw new ArgumentNullException(nameof(startupLoaderService));
            _dbOptions = dbOptions;
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
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
                "Waiting 10 sec to ensure all Network Dependancies available. \n\n");
            Thread.Sleep(10 * 1000);

            //####################### WE INITIALIZE THE STARTUP CONFIGURATION HERE ##########################
            // ################ Loading Configuration.

            await _startupLoaderService.LoadAllStartupDataAsync();


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
            _logger.Information("|     Confluence Sync Service     |");
            _logger.Information("|                                            |");
            _logger.Information($"|        Application version: {assemblyVersion}        |");
            _logger.Information("|____________________________________________|");
            _logger.Information("\r\n\r\n");

        }




        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            _logger.Information("Entering Main Worker Service ExecuteAsync Method\n");

            //Get the wait period at the bottom of each loop
            var tD = StartupConfiguration.GetProtectedSetting("TimeDelay");
            int timeDelay = Int32.Parse(tD) * 1000;

            _logger.Information(">>> About to get access token...");

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
                _logger.Information("Worker running at: {time}", DateTimeOffset.Now);

                try
                {
                    if (Authenticate.GetExpiresOn() > DateTime.UtcNow)
                    {
                        // Just to gove some indication on the console that the loop is still running
                        _logger.Information("Worker running at: {time}", DateTimeOffset.Now);

                        #region Plugin Loader Place Holder
                        #endregion

                        #region Place Holder for checking the mailbox associated with the service
                        #endregion

                        await Task.Delay(timeDelay, cancellationToken); //Pausing for seconds set in SQL DB ConfigStore Table under TimeDelay

                    }
                    else
                    {
                        if (cancellationToken.IsCancellationRequested || _cancellationTokenSource.IsCancellationRequested ||
                            cancelTokenIssued == true)
                        {
                            _logger.Information("\n ... Cancellation requested. Exiting worker service loop... \n");
                            break;
                        }

                        await Task.Delay(500, cancellationToken);
                        await _confidentialClientApp.GetAccessToken();
                        _logger.Debug("Aquired Bearer Token");
                    }

                }

                catch (OperationCanceledException)
                {
                    _logger.Information("Operation canceled. Exiting loop.");
                    break; // Exit the loop on cancellation
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Worker.cs failed to acquire an Access Bearer Token. Exception details:");
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
