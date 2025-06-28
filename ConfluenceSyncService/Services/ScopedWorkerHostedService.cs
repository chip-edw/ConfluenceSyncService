namespace ConfluenceSyncService.Services
{
    public class ScopedWorkerHostedService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        private CancellationTokenSource _cts;
        private Task _executingTask;

        public ScopedWorkerHostedService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            _executingTask = Task.Run(async () =>
            {
                using var scope = _serviceProvider.CreateScope();
                var worker = scope.ServiceProvider.GetRequiredService<IWorkerService>();

                //Setup and start the Core Application
                if (worker is Worker actualWorker)
                {
                    await actualWorker.StartAsync(_cts.Token); // Important! runs setup fop App....

                }

                await worker.DoWorkAsync(_cts.Token);
            });

            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _cts?.Cancel();

            if (_executingTask != null)
            {
                await Task.WhenAny(_executingTask, Task.Delay(Timeout.Infinite, cancellationToken));
            }
        }
    }
}
