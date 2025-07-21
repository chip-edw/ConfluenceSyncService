namespace ConfluenceSyncService.Models
{
    public class ConfluenceTableRow
    {
        public string PageId { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public string PageUrl { get; set; } = string.Empty;
        public DateTime LastModifiedUtc { get; set; } = DateTime.MinValue;
        public int PageVersion { get; set; } = 0;

        // Table fields matching SharePoint schema
        public string Region { get; set; } = string.Empty;
        public string StatusFF { get; set; } = string.Empty;
        public string StatusCust { get; set; } = string.Empty;
        public string Phase { get; set; } = string.Empty;
        public string GoLiveDate { get; set; } = string.Empty;
        public string SupportGoLiveDate { get; set; } = string.Empty;
        public string SupportImpact { get; set; } = string.Empty;
        public string SupportAccepted { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public string SyncTracker { get; set; } = string.Empty;

        // Helper method to convert to dictionary for SharePoint sync using configuration
        public Dictionary<string, object> ToSharePointFields(IConfiguration configuration, string listName = "TransitionTracker")
        {
            var fields = new Dictionary<string, object>();

            // Get field mappings from configuration
            var fieldMappings = configuration.GetSection($"SharePointFieldMappings:{listName}")
                .Get<Dictionary<string, string>>() ?? new Dictionary<string, string>();

            // Helper to get the SharePoint field name
            string GetFieldName(string displayName) => fieldMappings.GetValueOrDefault(displayName, displayName);

            // Map fields using configuration
            if (!string.IsNullOrEmpty(CustomerName))
                fields[GetFieldName("Title")] = CustomerName;

            if (!string.IsNullOrEmpty(Region))
                fields[GetFieldName("Region")] = Region;

            if (!string.IsNullOrEmpty(StatusFF))
                fields[GetFieldName("StatusFF")] = StatusFF;

            if (!string.IsNullOrEmpty(StatusCust))
                fields[GetFieldName("StatusCust")] = StatusCust;

            if (!string.IsNullOrEmpty(Phase))
                fields[GetFieldName("Phase")] = Phase;

            if (!string.IsNullOrEmpty(SupportImpact))
                fields[GetFieldName("SupportImpact")] = SupportImpact;

            // FIX: Ensure SupportAccepted is always included
            var supportAccepted = ParseBoolOrNull(SupportAccepted);
            if (supportAccepted.HasValue)
            {
                fields[GetFieldName("SupportAccepted")] = supportAccepted.Value;
            }
            else
            {
                // Include with null/default value if not parseable
                fields[GetFieldName("SupportAccepted")] = false; // or null depending on SharePoint field requirements
            }

            if (!string.IsNullOrEmpty(Notes))
                fields[GetFieldName("Notes")] = Notes;

            if (!string.IsNullOrEmpty(PageUrl))
                fields[GetFieldName("CustomerWiki")] = PageUrl;

            // FIX: Always include date fields, even if null/empty
            var goLiveDate = ParseDateOrNull(GoLiveDate);
            if (goLiveDate.HasValue)
            {
                fields[GetFieldName("GoLiveDate")] = goLiveDate.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }
            else
            {
                // Include with null value to ensure field appears in payload
                fields[GetFieldName("GoLiveDate")] = null;
            }

            var supportGoLiveDate = ParseDateOrNull(SupportGoLiveDate);
            if (supportGoLiveDate.HasValue)
            {
                fields[GetFieldName("SupportGoLiveDate")] = supportGoLiveDate.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }
            else
            {
                // Include with null value to ensure field appears in payload
                fields[GetFieldName("SupportGoLiveDate")] = null;
            }

            // Boolean field
            var syncTracker = ParseBoolOrNull(SyncTracker);
            if (syncTracker.HasValue)
                fields[GetFieldName("SyncTracker")] = syncTracker.Value;

            // PageId field
            if (int.TryParse(PageId, out var pageIdInt))
                fields[GetFieldName("ConfluencePageId")] = pageIdInt;

            return fields;
        }

        // Alternative method with exact field names (updated to include missing fields)
        public Dictionary<string, object> ToSharePointFieldsSimple()
        {
            var fields = new Dictionary<string, object>();

            // Only include non-null/non-empty values
            if (!string.IsNullOrEmpty(CustomerName))
                fields["Title"] = CustomerName;

            if (!string.IsNullOrEmpty(Region))
                fields["Region"] = Region;

            if (!string.IsNullOrEmpty(StatusFF))
                fields["StatusFF"] = StatusFF;

            if (!string.IsNullOrEmpty(StatusCust))
                fields["StatusCust"] = StatusCust;

            if (!string.IsNullOrEmpty(Phase))
                fields["Phase"] = Phase;

            // FIX: Always include date fields with proper SharePoint field names
            var goLiveDate = ParseDateOrNull(GoLiveDate);
            if (goLiveDate.HasValue)
            {
                fields["Go_x002d_LiveDate"] = goLiveDate.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }
            else
            {
                fields["Go_x002d_LiveDate"] = null;
            }

            var supportGoLiveDate = ParseDateOrNull(SupportGoLiveDate);
            if (supportGoLiveDate.HasValue)
            {
                fields["SupportGo_x002d_LiveDate"] = supportGoLiveDate.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }
            else
            {
                fields["SupportGo_x002d_LiveDate"] = null;
            }

            if (!string.IsNullOrEmpty(SupportImpact))
                fields["SupportImpact"] = SupportImpact;

            // FIX: Always include SupportAccepted field
            var supportAccepted = ParseBoolOrNull(SupportAccepted);
            if (supportAccepted.HasValue)
            {
                fields["field_8"] = supportAccepted.Value;
            }
            else
            {
                fields["field_8"] = false; // Default value
            }

            if (!string.IsNullOrEmpty(Notes))
                fields["Notes"] = Notes;

            if (!string.IsNullOrEmpty(PageUrl))
                fields["CustomerWiki"] = PageUrl;

            var syncTracker = ParseBoolOrNull(SyncTracker);
            if (syncTracker.HasValue)
                fields["SyncTracker"] = syncTracker.Value;

            if (int.TryParse(PageId, out var pageIdInt))
                fields["ConfluencePageId"] = pageIdInt;

            return fields;
        }

        // Enhanced method with debugging capabilities
        public Dictionary<string, object> ToSharePointFieldsWithDebug(IConfiguration configuration, string listName = "TransitionTracker")
        {
            var fields = new Dictionary<string, object>();

            // Get field mappings from configuration
            var fieldMappings = configuration.GetSection($"SharePointFieldMappings:{listName}")
                .Get<Dictionary<string, string>>() ?? new Dictionary<string, string>();

            // Helper to get the SharePoint field name
            string GetFieldName(string displayName) => fieldMappings.GetValueOrDefault(displayName, displayName);

            // Debug: Log what we're trying to map
            Console.WriteLine($"DEBUG: Mapping fields for {CustomerName}");
            Console.WriteLine($"  GoLiveDate source: '{GoLiveDate}'");
            Console.WriteLine($"  SupportGoLiveDate source: '{SupportGoLiveDate}'");
            Console.WriteLine($"  SupportAccepted source: '{SupportAccepted}'");
            Console.WriteLine($"  Available field mappings: {string.Join(", ", fieldMappings.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");

            // Map all fields, including null values for missing required fields
            if (!string.IsNullOrEmpty(CustomerName))
                fields[GetFieldName("Title")] = CustomerName;

            if (!string.IsNullOrEmpty(Region))
                fields[GetFieldName("Region")] = Region;

            if (!string.IsNullOrEmpty(StatusFF))
                fields[GetFieldName("StatusFF")] = StatusFF;

            if (!string.IsNullOrEmpty(StatusCust))
                fields[GetFieldName("StatusCust")] = StatusCust;

            if (!string.IsNullOrEmpty(Phase))
                fields[GetFieldName("Phase")] = Phase;

            if (!string.IsNullOrEmpty(SupportImpact))
                fields[GetFieldName("SupportImpact")] = SupportImpact;

            if (!string.IsNullOrEmpty(Notes))
                fields[GetFieldName("Notes")] = Notes;

            if (!string.IsNullOrEmpty(PageUrl))
                fields[GetFieldName("CustomerWiki")] = PageUrl;

            // ALWAYS include these fields, even with null values
            var goLiveDate = ParseDateOrNull(GoLiveDate);
            var goLiveDateFieldName = GetFieldName("GoLiveDate");
            fields[goLiveDateFieldName] = goLiveDate?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            Console.WriteLine($"  Mapped GoLiveDate to '{goLiveDateFieldName}': {fields[goLiveDateFieldName]}");
            Console.WriteLine($"  Parsed date value: {goLiveDate}");

            var supportGoLiveDate = ParseDateOrNull(SupportGoLiveDate);
            var supportGoLiveDateFieldName = GetFieldName("SupportGoLiveDate");
            fields[supportGoLiveDateFieldName] = supportGoLiveDate?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            Console.WriteLine($"  Mapped SupportGoLiveDate to '{supportGoLiveDateFieldName}': {fields[supportGoLiveDateFieldName]}");
            Console.WriteLine($"  Parsed support date value: {supportGoLiveDate}");

            var supportAccepted = ParseBoolOrNull(SupportAccepted);
            var supportAcceptedFieldName = GetFieldName("SupportAccepted");
            fields[supportAcceptedFieldName] = supportAccepted ?? false;
            Console.WriteLine($"  Mapped SupportAccepted to '{supportAcceptedFieldName}': {fields[supportAcceptedFieldName]}");
            Console.WriteLine($"  Parsed boolean value: {supportAccepted}");

            // Other fields
            var syncTracker = ParseBoolOrNull(SyncTracker);
            if (syncTracker.HasValue)
                fields[GetFieldName("SyncTracker")] = syncTracker.Value;

            if (int.TryParse(PageId, out var pageIdInt))
                fields[GetFieldName("ConfluencePageId")] = pageIdInt;

            Console.WriteLine($"DEBUG: Final field count: {fields.Count}");
            Console.WriteLine($"DEBUG: All fields being sent to SharePoint:");
            foreach (var field in fields)
            {
                Console.WriteLine($"  '{field.Key}' = '{field.Value}' (Type: {field.Value?.GetType().Name ?? "null"})");
            }

            return fields;
        }

        // Force sync method that always includes the problematic fields
        public Dictionary<string, object> ToSharePointFieldsForceSync(IConfiguration configuration, string listName = "TransitionTracker")
        {
            var fields = ToSharePointFields(configuration, listName);

            // Force include the missing fields with their expected SharePoint names
            var fieldMappings = configuration.GetSection($"SharePointFieldMappings:{listName}")
                .Get<Dictionary<string, string>>() ?? new Dictionary<string, string>();

            string GetFieldName(string displayName) => fieldMappings.GetValueOrDefault(displayName, displayName);

            // Force the date fields with explicit SharePoint field names
            var goLiveDate = ParseDateOrNull(GoLiveDate);
            var supportGoLiveDate = ParseDateOrNull(SupportGoLiveDate);
            var supportAccepted = ParseBoolOrNull(SupportAccepted);

            // Try both the mapped name AND the expected SharePoint internal names
            var goLiveDateField = GetFieldName("GoLiveDate");
            var supportGoLiveDateField = GetFieldName("SupportGoLiveDate");
            var supportAcceptedField = GetFieldName("SupportAccepted");

            // Add with mapped names
            fields[goLiveDateField] = goLiveDate?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            fields[supportGoLiveDateField] = supportGoLiveDate?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            fields[supportAcceptedField] = supportAccepted ?? false;

            // Also try with the expected SharePoint internal names from your JSON
            fields["Go_x002d_LiveDate"] = goLiveDate?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            fields["SupportGo_x002d_LiveDate"] = supportGoLiveDate?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            fields["field_8"] = supportAccepted ?? false;

            return fields;
        }

        // Test method to debug parsing issues
        public void TestParsing()
        {
            Console.WriteLine($"=== TESTING PARSING FOR {CustomerName} ===");
            Console.WriteLine($"GoLiveDate: '{GoLiveDate}' (Length: {GoLiveDate?.Length ?? 0})");
            Console.WriteLine($"SupportGoLiveDate: '{SupportGoLiveDate}' (Length: {SupportGoLiveDate?.Length ?? 0})");
            Console.WriteLine($"SupportAccepted: '{SupportAccepted}' (Length: {SupportAccepted?.Length ?? 0})");

            // Test date parsing
            var goLive = ParseDateOrNull(GoLiveDate);
            var supportGoLive = ParseDateOrNull(SupportGoLiveDate);
            var supportAccepted = ParseBoolOrNull(SupportAccepted);

            Console.WriteLine($"Parsed GoLiveDate: {goLive}");
            Console.WriteLine($"Parsed SupportGoLiveDate: {supportGoLive}");
            Console.WriteLine($"Parsed SupportAccepted: {supportAccepted}");

            // Check for hidden characters
            if (!string.IsNullOrEmpty(GoLiveDate))
            {
                Console.WriteLine($"GoLiveDate bytes: {string.Join(",", System.Text.Encoding.UTF8.GetBytes(GoLiveDate))}");
            }
            if (!string.IsNullOrEmpty(SupportAccepted))
            {
                Console.WriteLine($"SupportAccepted bytes: {string.Join(",", System.Text.Encoding.UTF8.GetBytes(SupportAccepted))}");
            }
        }

        private DateTime? ParseDateOrNull(string dateString)
        {
            if (string.IsNullOrEmpty(dateString) ||
                dateString.Trim() == "" ||
                dateString == "N/A" ||
                dateString == "TBD" ||
                dateString == "YYYY-MM-DD") // Keep this as a template indicator
                return null;

            // Try to parse the date
            if (DateTime.TryParse(dateString, out var date))
            {
                // If the date was parsed successfully but has no time component,
                // ensure it has a time component for SharePoint (use noon UTC)
                if (date.TimeOfDay == TimeSpan.Zero)
                {
                    date = date.AddHours(12); // Set to noon to avoid timezone issues
                }
                return date;
            }

            // Try specific date formats that might not parse automatically
            string[] formats = {
                "yyyy-MM-dd",
                "MM/dd/yyyy",
                "dd/MM/yyyy",
                "yyyy/MM/dd",
                "MM-dd-yyyy",
                "dd-MM-yyyy"
            };

            foreach (var format in formats)
            {
                if (DateTime.TryParseExact(dateString, format, null, System.Globalization.DateTimeStyles.None, out date))
                {
                    // Add noon time component
                    return date.AddHours(12);
                }
            }

            return null;
        }

        private bool? ParseBoolOrNull(string boolString)
        {
            if (string.IsNullOrEmpty(boolString))
                return null;

            return boolString.Trim().ToLowerInvariant() switch
            {
                "yes" => true,
                "no" => false,
                "true" => true,
                "false" => false,
                "1" => true,
                "0" => false,
                _ => null
            };
        }
    }
}