using Microsoft.EntityFrameworkCore;
using Serilog;
namespace ConfluenceSyncService.Models
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
            //Empty constructor body. Was bugging me so I put this comment here.
        }
        public DbSet<ConfigStore> ConfigStore { get; set; }
        public DbSet<TableSyncState> TableSyncStates { get; set; }
        public DbSet<SyncState> SyncStates { get; set; }
        public DbSet<TaskIdMap> TaskIdMaps => Set<TaskIdMap>();



        public override int SaveChanges()
        {
            UpdateTimestamps();
            return base.SaveChanges();
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            UpdateTimestamps();
            return await base.SaveChangesAsync(cancellationToken);
        }

        private void UpdateTimestamps()
        {
            var entries = ChangeTracker.Entries<ConfigStore>();
            var now = DateTime.UtcNow;

            Console.WriteLine($"UpdateTimestamps called - found {entries.Count()} tracked entities");

            foreach (var entry in entries)
            {
                Console.WriteLine($"Entity {entry.Entity.ValueName ?? "Unknown"} has state: {entry.State}");

                switch (entry.State)
                {
                    case EntityState.Added:
                        entry.Entity.CreatedAt = now;
                        entry.Entity.UpdatedAt = now;
                        Console.WriteLine($"Set timestamps for new record: {entry.Entity.ValueName}");
                        break;
                    case EntityState.Modified:
                        entry.Entity.UpdatedAt = now;
                        Console.WriteLine($"Updated timestamp for modified record: {entry.Entity.ValueName} to {now}");
                        break;
                }
            }
        }

        #region OnConfiguring Method
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Get the directory of the executing assembly
            var exePath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (exePath == null)
            {
                // Log the error using Serilog and throw an exception
                Log.Error("The executable path could not be determined. Ensure the assembly location is accessible.");
                throw new InvalidOperationException("The executable path cannot be null.");
            }
            if (!optionsBuilder.IsConfigured)
            {
                IConfigurationRoot configuration = new ConfigurationBuilder()
                    .SetBasePath(exePath)
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();
                var connectionString = configuration.GetConnectionString("DefaultConnection");
                optionsBuilder.UseSqlite(connectionString);
            }
        }
        #endregion

        #region OnModelCreating Method
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<ConfigStore>(entity =>
            {
                // Configure the timestamps
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .ValueGeneratedOnAdd();

                entity.Property(e => e.UpdatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
            });

            modelBuilder.Entity<SyncState>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.SharePointId).IsRequired(false);
                entity.Property(e => e.ConfluenceId).IsRequired(false);
                entity.Property(e => e.LastSharePointModifiedUtc).IsRequired(false);
                entity.Property(e => e.LastConfluenceModifiedUtc).IsRequired(false);
                entity.Property(e => e.LastSyncedUtc).IsRequired(false);
                entity.Property(e => e.LastSource).IsRequired(false);

                // Optional: Add a unique constraint if you want to prevent duplicate pairs
                entity.HasIndex(e => new { e.SharePointId, e.ConfluenceId }).IsUnique(false);
            });

            modelBuilder.Entity<TableSyncState>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.ConfluencePageId).IsRequired();
                entity.Property(e => e.CustomerName).IsRequired();
                entity.HasIndex(e => e.ConfluencePageId).IsUnique();
            });

            modelBuilder.Entity<TaskIdMap>(entity =>
            {
                entity.ToTable("TaskIdMap");
                entity.HasKey(e => e.TaskId);

                entity.Property(e => e.TaskId).ValueGeneratedOnAdd(); // AUTOINCREMENT on SQLite
                entity.Property(e => e.ListKey).HasDefaultValue("PhaseTasks").IsRequired();
                entity.Property(e => e.State).HasDefaultValue("reserved").IsRequired();
                entity.Property(e => e.CreatedUtc).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.AckVersion).HasDefaultValue(1).IsRequired();

                // C2 fields used by the chaser
                entity.Property(e => e.NextChaseAtUtcCached); // DateTimeOffset?
                entity.Property(e => e.LastChaseAtUtc);       // DateTimeOffset?
                entity.Property(e => e.Region).HasMaxLength(64);
                entity.Property(e => e.AnchorDateType).HasMaxLength(64);

                // Existing indexes
                entity.HasIndex(e => e.SpItemId).IsUnique(); // SQLite allows multiple NULLs
                entity.HasIndex(e => e.CorrelationId);
                entity.HasIndex(e => new { e.CustomerId, e.PhaseName, e.TaskName, e.WorkflowId });

                // Helpful for channel lookups if needed later
                entity.HasIndex(e => new { e.TeamId, e.ChannelId });

                // C2 helper indexes
                entity.HasIndex(e => e.NextChaseAtUtcCached)
                      .HasDatabaseName("IX_TaskIdMap_NextChaseAtUtcCached");
                entity.HasIndex(e => e.AckExpiresUtc)
                      .HasDatabaseName("IX_TaskIdMap_AckExpiresUtc");
            });


        }
        #endregion

    }
}
