namespace ConfluenceSyncService.Models
{
    public class SharePointFieldMappings
    {
        public Dictionary<string, Dictionary<string, string>> Lists { get; set; } = new();

        // Helper method to get field mappings for a specific list
        public Dictionary<string, string> GetListMappings(string listName)
        {
            return Lists.GetValueOrDefault(listName, new Dictionary<string, string>());
        }

        // Helper method to get SharePoint field name from display name
        public string GetSharePointFieldName(string listName, string displayName)
        {
            var listMappings = GetListMappings(listName);
            return listMappings.GetValueOrDefault(displayName, displayName);
        }
    }
}