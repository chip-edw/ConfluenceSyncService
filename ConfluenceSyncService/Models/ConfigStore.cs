namespace ConfluenceSyncService.Models
{
    // Used for the configurations that are needed to successfully start the application.
    // At Service start the configurations will be loaded into a Dictionary for reference as needed.
    public class ConfigStore
    {
        public int Id { get; set; }
        public string ValueName { get; set; } = string.Empty;
        public string ValueType { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}
