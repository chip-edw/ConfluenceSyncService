using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ConfluenceSyncService.Models;

[Table("TaskIdMap")]
public class TaskIdMap
{
    [Key]
    public int TaskId { get; set; } // INTEGER PRIMARY KEY AUTOINCREMENT

    [Required]
    public string ListKey { get; set; } = "PhaseTasks";

    // SharePoint item id once created
    public string? SpItemId { get; set; }

    // Fast lookup when anchors have not changed
    public string? CorrelationId { get; set; }

    // Deterministic natural key parts (anchor-proof)
    public string? CustomerId { get; set; }
    public string? PhaseName { get; set; }
    public string? TaskName { get; set; }
    public string? WorkflowId { get; set; }

    // Lifecycle: reserved → linked
    [Required]
    public string State { get; set; } = "reserved";

    // Reservation timestamp
    [Required]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // Teams channel threading (future steps use these)
    public string? TeamId { get; set; }
    public string? ChannelId { get; set; }
    public string? RootMessageId { get; set; }  // first post id for the task
    public string? LastMessageId { get; set; }  // last post in the thread (optional)

    // ACK link control
    public int AckVersion { get; set; } = 1;            // rotate per send to invalidate older links
    public DateTime? AckExpiresUtc { get; set; }        // optional bookkeeping for current link expiry
}
