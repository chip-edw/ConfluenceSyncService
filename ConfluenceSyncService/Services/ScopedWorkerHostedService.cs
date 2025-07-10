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
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var worker = scope.ServiceProvider.GetRequiredService<IWorkerService>();

                    if (worker is Worker actualWorker)
                        await actualWorker.StartAsync(_cts.Token);

                    await worker.DoWorkAsync(_cts.Token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FATAL ScopedWorkerHostedService] Exception: {ex}");
                    throw; // rethrow so host knows startup failed
                }
            });

            return _executingTask; // ← THIS IS THE KEY FIX
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
