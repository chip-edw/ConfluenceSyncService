namespace ConfluenceSyncService.Models;

/// <summary>
/// Standard task status values used across SharePoint, SQLite cache, and workflow logic.
/// Centralizes status strings to prevent typos and ensure consistency.
/// </summary>
public static class TaskStatus
{
    public const string NotStarted = "Not Started";
    public const string InProgress = "In Progress";
    public const string Completed = "Completed";
}
