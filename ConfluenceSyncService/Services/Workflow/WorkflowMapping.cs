using System.Text.Json;

namespace ConfluenceSyncService.Services.Workflow
{
    public sealed class WorkflowMapping
    {
        public int Version { get; set; }
        public string WorkflowId { get; set; } = "";
        public Dictionary<string, ListMap> Lists { get; set; } = new();
        public MappingKeys Keys { get; set; } = new();
        public PhaseIdentity PhaseIdentity { get; set; } = new();
        public Idempotency Idempotency { get; set; } = new();
        // Keep the rest of the JSON accessible if needed
        public JsonElement Raw { get; set; }
    }

    public sealed class ListMap
    {
        public string Name { get; set; } = "";
        public string IdField { get; set; } = "";
        public Dictionary<string, string> Columns { get; set; } = new();
        public JsonElement? Resolvers { get; set; }
    }

    public sealed class MappingKeys
    {
        public string CustomerId { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string Region { get; set; } = "";
        public string PhaseName { get; set; } = "";
        public string GoLive { get; set; } = "";
        public string HypercareEnd { get; set; } = "";
        public string TrackerRowId { get; set; } = "";
    }

    public sealed class PhaseIdentity
    {
        public List<string> LookupBy { get; set; } = new();
        public string IdField { get; set; } = "phaseId";
        public string Generate { get; set; } = "guid";
    }

    public sealed class Idempotency
    {
        public string Format { get; set; } = "{{customerId}}|{{phaseId}}|{{workflowId}}|{{activity.key}}";
        public string Hash { get; set; } = "sha1";
    }
}
