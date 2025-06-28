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
        }
        #endregion

    }
}
