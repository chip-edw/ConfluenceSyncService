namespace ConfluenceSyncService.SharePoint
{
    public sealed class SharePointFieldMappingsOptions
    {
        public Dictionary<string, string> Map { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string Get(string logical) => Map.TryGetValue(logical, out var v) ? v : logical;
    }

}
