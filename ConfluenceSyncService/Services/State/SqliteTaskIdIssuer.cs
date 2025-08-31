using ConfluenceSyncService.Interfaces;
using ConfluenceSyncService.Models;
using Microsoft.EntityFrameworkCore;

namespace ConfluenceSyncService.Services.State
{
    public sealed class SqliteTaskIdIssuer : ITaskIdIssuer
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SqliteTaskIdIssuer> _log;

        public SqliteTaskIdIssuer(IServiceScopeFactory scopeFactory, ILogger<SqliteTaskIdIssuer> log)
        {
            _scopeFactory = scopeFactory;
            _log = log;
        }

        public async Task<int> ReserveAsync(
            string listKey,
            string? correlationId,
            string? customerId,
            string? phaseName,
            string? taskName,
            string? workflowId,
            CancellationToken ct = default)
        {
            // Reuse an existing reserved row if present for this correlation
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // First try to reuse a reserved row
            var existing = await db.TaskIdMaps.AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.ListKey == listKey &&
                    x.CorrelationId == correlationId &&
                    x.State == "reserved",
                    ct);

            if (existing != null)
            {
                _log.LogInformation("TaskIdIssuer.Reserve - reused reserved TaskId {TaskId} for {ListKey}/{CorrelationId}", existing.TaskId, listKey, correlationId);
                return existing.TaskId;
            }

            // Create a new reservation
            var map = new TaskIdMap
            {
                ListKey = listKey,
                CorrelationId = correlationId,
                CustomerId = customerId,
                PhaseName = phaseName,
                TaskName = taskName,
                WorkflowId = workflowId,
                State = "reserved",
                CreatedUtc = DateTime.UtcNow
            };

            // Light retry in case of SQLITE_BUSY
            var attempt = 0;
            while (true)
            {
                attempt++;
                try
                {
                    db.TaskIdMaps.Add(map);
                    await db.SaveChangesAsync(ct);
                    _log.LogInformation("TaskIdIssuer.Reserve - issued TaskId {TaskId} for {ListKey}/{CorrelationId}", map.TaskId, listKey, correlationId);
                    return map.TaskId;
                }
                catch (DbUpdateException ex) when (IsBusy(ex) && attempt <= 3)
                {
                    _log.LogWarning(ex, "TaskIdIssuer.Reserve - SQLITE_BUSY, retrying attempt {Attempt}", attempt);
                    await Task.Delay(50 * attempt, ct);
                    db.Entry(map).State = EntityState.Added; // ensure still Added
                    continue;
                }
            }
        }

        public async Task LinkToSharePointAsync(int taskId, string spItemId, CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var row = await db.TaskIdMaps.FirstOrDefaultAsync(x => x.TaskId == taskId, ct);
            if (row == null)
            {
                throw new InvalidOperationException($"TaskId {taskId} not found to link.");
            }

            row.SpItemId = spItemId;
            row.State = "linked";

            // Optionally initialize AckVersion and AckExpiresUtc here if you want
            // row.AckVersion = Math.Max(1, row.AckVersion);
            // row.AckExpiresUtc = null;

            var attempt = 0;
            while (true)
            {
                attempt++;
                try
                {
                    await db.SaveChangesAsync(ct);
                    _log.LogInformation("TaskIdIssuer.Link - linked TaskId {TaskId} to SpItemId {SpItemId}", taskId, spItemId);
                    return;
                }
                catch (DbUpdateException ex) when (IsBusy(ex) && attempt <= 3)
                {
                    _log.LogWarning(ex, "TaskIdIssuer.Link - SQLITE_BUSY, retrying attempt {Attempt}", attempt);
                    await Task.Delay(50 * attempt, ct);
                    db.Entry(row).State = EntityState.Modified;
                    continue;
                }
                catch (DbUpdateException ex) when (IsUniqueViolationOnSpItemId(ex))
                {
                    // If another thread linked first, we are fine. Reload to keep state consistent.
                    _log.LogWarning(ex, "TaskIdIssuer.Link - SpItemId already linked by another process. Reloading.");
                    await db.Entry(row).ReloadAsync(ct);
                    return;
                }
            }
        }

        private static bool IsBusy(DbUpdateException ex)
            => ex.InnerException?.Message.IndexOf("database is locked", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsUniqueViolationOnSpItemId(DbUpdateException ex)
            => ex.InnerException?.Message.IndexOf("UNIQUE constraint failed: TaskIdMap.SpItemId", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
