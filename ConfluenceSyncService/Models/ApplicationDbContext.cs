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
        }
        #endregion
    }
}