# Sync Architecture Documentation

## Overview

This document details the bidirectional synchronization architecture between SharePoint Lists and Confluence Tables, including sync triggers, field mapping, and change detection mechanisms.  
It also documents the **phase-based workflow orchestration** that controls task notifications and human acknowledgements. Timezone for workflow date calculations is **America/Chicago**.

## Sync Flow Architecture

### High-Level Workflow

```
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   Confluence    │◄──►│  Sync Engine     │◄──►│   SharePoint    │
│     Tables      │    │  (Worker Service)│    │     Lists       │
└─────────────────┘    └──────────────────┘    └─────────────────┘
         │                       │                       │
         ▼                       ▼                       ▼
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│ Page Timestamp  │    │  TableSyncState  │    │ Item Timestamp  │
│  Tracking       │    │    Database      │    │   Tracking      │
└─────────────────┘    └──────────────────┘    └─────────────────┘
```

## Workflow Orchestration (Phase / Category / Parallelism)

**Definitions**
- **AnchorDateType**: which anchor to use for offsets (e.g., `GoLiveDate` or `SupportGoLiveDate`).
- **StartOffsetDays**: business-day offset from the selected anchor.
- **TaskCategory**: a logical grouping that must complete before the next category can begin.
- **Parallel Tasks**: tasks that share the same `(TaskCategory, AnchorDateType, StartOffsetDays)`.

**Core Rule (current design)**  
Within a given **Phase**, the engine progresses **category by category**. Inside a category, it advances **group by group**, where a **group** is defined by:
```
(TaskCategory, AnchorDateType, StartOffsetDays)
```
- All tasks in the **current group** can be worked **in parallel**.
- The **next group** in the same category is **not eligible** until **all tasks** in the current group are `Completed`.
- The **next TaskCategory** is **not eligible** until the **current TaskCategory** is fully completed.
- A group is **eligible to notify** when:
  ```
  AnchorDate(GoLiveDate or SupportGoLiveDate) + StartOffsetDays <= now (America/Chicago)
  ```

**Idempotency**
- Human ACKs are double-click safe.
- Notification stamps prevent duplicate sends for the same task instance.

## Current Implementation Snapshot

- **Mapping loader**: `mapping.transition.json` via `WorkflowMappingProvider`. Keys:  
  `transitionTracker`, `transitionCustomers`, `phaseTasks`, `transitionResources`.
- **Cursor store**: `%LOCALAPPDATA%/ConfluenceSyncService/cursors.json`  
  Key: `Cursor:TransitionTracker:lastModifiedUtc`.
- **Step 5 (deltas)**: Read Transition Tracker via `siteId + GetAllListItemsAsync`, filter `LastModifiedUtc > cursor`.
- **Step 6 (per item)**:
  - Backfill **CustomerId** on Transition Tracker (reuse from `TransitionCustomers` by name; else deterministic GUID v5). **Read-after-write** verifies; if it fails, **cursor does not advance**.
  - Upsert **TransitionCustomers** (`CustomerId`, `Customer/Title`, `Region`, `ActivePhaseID`).
  - Upsert **Phase Tasks & Metadata** rows keyed by  
    `CorrelationId = sha1(CustomerId|PhaseId|WorkflowId|ActivityKey)`, default `Status="NotStarted"`.
  - Advance cursor to the **max processed LastModifiedUtc only when no blockers**.

> **SharePoint writes**: always use `SharePointFieldMappings` internal names; **never** write read-only/view fields (e.g., `LinkTitle`, `Modified`).

## Sync Triggers

### Confluence → SharePoint

**Trigger Mechanism**: Page-level timestamp comparison

```csharp
// Triggers when Confluence page is modified
if (confluenceRow.LastModifiedUtc > syncState.LastConfluenceModifiedUtc)
{
    // Sync entire table row to SharePoint
}
```

**Characteristics**:
- **Detection Level**: Entire Confluence page
- **Sync Scope**: Complete table row (all fields)
- **Frequency**: Any change to the Confluence page triggers sync
- **Field Granularity**: All fields are synced, regardless of which specific field changed

### SharePoint → Confluence  

**Trigger Mechanism**: Item-level timestamp comparison

```csharp
// Triggers when SharePoint list item is modified  
if (spItem.LastModifiedUtc > syncState.LastSharePointModifiedUtc)
{
    // Sync entire row to Confluence
}
```

**Characteristics**:
- **Detection Level**: Individual SharePoint list item
- **Sync Scope**: Complete table row (all fields)
- **Frequency**: Any field change in SharePoint list item triggers sync
- **Field Granularity**: All fields processed, but only changed fields updated in Confluence

## Conditional Sync Control

### Bidirectional Sync Tracker

Both platforms can control sync behavior using their respective `SyncTracker` fields:

**SharePoint → Confluence Control**:
```csharp
// Only sync if SharePoint SyncTracker = "Yes"/"True"/"1"
if (!ShouldSyncBasedOnSyncTracker(spItem))
{
    // Skip sync
    return false;
}
```

**Confluence → SharePoint Control**:
```csharp  
// Only sync if Confluence Sync Tracker = "Yes"/"True"/"1"
if (!ShouldSyncBasedOnConfluenceSyncTracker(confluenceRow))
{
    // Skip sync
    return false;
}
```

**Sync Control Matrix**:
| SharePoint SyncTracker | Confluence Sync Tracker | Result |
|----------------------|-------------------------|---------|
| Yes | Yes | Full bidirectional sync |
| Yes | No | Only SharePoint → Confluence |
| No | Yes | Only Confluence → SharePoint |
| No | No | No sync in either direction |

## Field Mapping Architecture

### Confluence → SharePoint Mapping

| Confluence Field | SharePoint Internal Name | Type | Notes |
|------------------|-------------------------|------|-------|
| Region | `field_1` | Choice | Color-coded status |
| Status FF | `field_2` | Choice | Color-coded status |
| Status Cust. | `field_3` | Choice | Color-coded status |
| Phase | `field_4` | Text | Free-form text |
| Support Impact | `field_7` | Choice | Color-coded status |
| Support Accepted | `field_8` | Choice | Yes/No/Pending |
| Notes | `field_9` | Text | Multi-line text |
| Go-Live Date (YYYY-MM-DD) | `Go_x002d_LiveDate` | DateTime | ISO 8601 format |
| Support Go-Live Date (YYYY-MM-DD) | `SupportGo_x002d_LiveDate` | DateTime | ISO 8601 format |
| Sync Tracker | `SyncTracker` | Boolean | Yes/No |
| CustomerName | `Title` | Text | SharePoint item title |
| PageId | `ConfluencePageId` | Number | Confluence page identifier |

### Data Type Transformations

#### Date Fields
```csharp
// Confluence: "2018-01-15" 
// SharePoint: "2018-01-15T12:00:00.000Z"

var goLiveDate = ParseDateOrNull(GoLiveDate);
if (goLiveDate.HasValue)
    fields["Go_x002d_LiveDate"] = goLiveDate.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
```

#### Choice Fields
```csharp
// Support Accepted (Choice field, not boolean):
// Confluence: "Yes", "No", "Pending" 
// SharePoint: "Yes", "No", "Pending"

// Direct mapping for choice fields
if (!string.IsNullOrEmpty(SupportAccepted))
    fields["field_8"] = SupportAccepted;
```

#### Boolean Fields  
```csharp
// Only SyncTracker is actually boolean:
// Confluence: "Yes", "No"
// SharePoint: true, false

var syncTracker = boolString?.ToLowerInvariant() switch
{
    "yes" => true,
    "no" => false,
    _ => false
};
```

## Placeholder Text Handling

### Template Default Replacement

When syncing to newly created Confluence pages from templates, the system intelligently replaces placeholder text with real data:

**Placeholder Detection**:
- Status fields: "⚠️ Select correct color", "Select Status"
- Date fields: "YYYY-MM-DD"  
- Text fields: "[Enter notes here]"
- Legend text: Any text containing "=" or "|"

**Replacement Logic**:
```csharp
private bool IsPlaceholderText(string text)
{
    if (string.IsNullOrEmpty(text))
        return false;
        
    var lowerText = text.ToLowerInvariant();
    
    return lowerText.Contains("select correct color") ||
           lowerText.Contains("⚠️") ||
           text == "YYYY-MM-DD" ||
           text == "[Enter notes here]" ||
           lowerText.Contains("select") && lowerText.Contains("color") ||
           lowerText.Contains("select status") ||
           lowerText.Contains("select impact") ||
           lowerText.Contains("select region") ||
           lowerText.Contains("select yes/no") ||
           lowerText.Contains("please select") ||
           text.Trim() == "";
}
```

**Behavior**:
- Placeholder text is always replaced with real data
- Real user data is preserved and not overwritten
- Legend text is never modified

## Change Detection Mechanisms

### Confluence Change Detection

**Method**: ADF (Atlassian Document Format) parsing with field-level comparison

```csharp
private bool UpdateCellValue(JToken cell, string fieldName, string newValue)
{
    // Skip legend text (contains = or |)
    if (currentText.Contains("=") || currentText.Contains("|"))
        continue;
        
    // Replace placeholder text with real data
    if (IsPlaceholderText(currentText) && !string.IsNullOrEmpty(newValue))
    {
        node["text"] = newValue;
        return true;
    }
    
    // Update if value actually changed
    if (currentText != newValue && !string.IsNullOrEmpty(newValue))
    {
        node["text"] = newValue;
        return true;
    }
    
    return false;
}
```

**Supported Field Types**:
- **Status Macros**: Color-coded fields with automatic color mapping and placeholder replacement
- **Text Fields**: Plain text with direct replacement and placeholder handling
- **Date Fields**: Text format with validation and placeholder detection

### SharePoint Change Detection

**Method**: Microsoft Graph API item timestamps

```csharp
// SharePoint provides item-level LastModifiedUtc
if (spItem.LastModifiedUtc > syncState.LastSharePointModifiedUtc)
{
    // Process all fields, but only update changed ones
}
```

## Sync State Management

### Database Schema

```sql
TableSyncState:
- ConfluencePageId (string)
- SharePointItemId (string) 
- LastSyncedUtc (DateTime?)
- LastConfluenceModifiedUtc (DateTime?)
- LastSharePointModifiedUtc (DateTime?)
- LastSyncSource (string) -- "Confluence" or "SharePoint"
- LastSyncStatus (string) -- "Success" or "Failed"
- LastErrorMessage (string?)
```

### Conflict Resolution

**Two-Cycle Stabilization Pattern**:

1. **Cycle 1**: SharePoint change triggers SharePoint → Confluence sync
2. **Cycle 2**: Confluence timestamp updated, triggers Confluence → SharePoint sync  
3. **Cycle 3**: No changes detected, system stabilized

This pattern ensures eventual consistency and handles edge cases gracefully.

## Error Handling

### Sync Failure Recovery

```csharp
try 
{
    // Perform sync operation
    syncState.LastSyncStatus = "Success";
    syncState.LastErrorMessage = null;
}
catch (Exception ex)
{
    syncState.LastSyncStatus = "Failed"; 
    syncState.LastErrorMessage = ex.Message;
    // Continue processing other items
}
```

### Field Mapping Failures

- **Missing Fields**: Logged as warnings, sync continues
- **Invalid Data Types**: Logged with specific error details
- **API Failures**: Retry logic with exponential backoff
- **Placeholder Detection**: Ensures template defaults don't overwrite real data

## Performance Characteristics

### Sync Frequency
- **Worker Service**: Runs every 60 seconds
- **Change Detection**: O(1) timestamp comparison per item
- **Field Processing**: O(n) where n = number of fields per item

### API Call Optimization
- **Confluence**: Batch page retrieval with expand parameters
- **SharePoint**: Microsoft Graph batch operations
- **Caching**: List metadata and field mappings cached

### Scalability Considerations
- **Memory**: Processes items sequentially to control memory usage
- **Rate Limits**: Respects SharePoint and Confluence API rate limits  
- **Concurrency**: Single-threaded processing prevents race conditions

## Configuration

### Field Mapping Configuration

```json
{
  "SharePointFieldMappings": {
    "TransitionTracker": {
      "GoLiveDate": "Go_x002d_LiveDate",
      "SupportGoLiveDate": "SupportGo_x002d_LiveDate",
      "SupportAccepted": "field_8"
    }
  }
}
```

### Color Mapping Configuration

```json
{
  "ConfluenceColorMappings": {
    "StatusFF": {
      "green": "Green",
      "yellow": "Amber", 
      "red": "Red"
    },
    "Region": {
      "green": "APAC",
      "purple": "NA",
      "yellow": "EMEA"
    },
    "SupportAccepted": {
      "green": "Yes",
      "yellow": "Pending",
      "red": "No"
    }
  }
}
```

### Notification Additions (MVP)

```json
{
  "PublicBaseUrl": "https://<public-host>",
  "Notifications": {
    "MarkCompleteLinkTtlMinutes": 120,
    "DefaultFallbackEmail": "me@example.com"
  }
}
```

## Notifications & Action Links (MVP)

- **Signed action links** via `SignatureService` with TTL.  
- **Management API** ACK endpoint verifies signature/expiry, idempotently sets:
  - `Status = "Completed"`, `CompletedDate = UtcNow`, optional `AckedBy`.
- **Teams notifications**:
  - Resolve assignee from **Transition Resources** by `Role + Region`, else use `Notifications.DefaultFallbackEmail`.
  - Send channel message containing a “Mark complete” link.
- **Auto-advance** (optional): when the last task in the current group completes, notify the next eligible group. When the category completes, the next category becomes eligible.

## Debugging and Monitoring

### Debug Logging

The system includes comprehensive debug logging:

```csharp
confluenceTableRow.TestParsing(); // Field mapping validation
_logger.Information("Updated {FieldName}: {OldValue} → {NewValue}", 
    fieldName, currentText, newValue);
_logger.Information("Replaced placeholder text in {FieldName}: '{PlaceholderText}' → '{NewValue}'",
    fieldName, currentText, newValue);
```

### Sync Status Tracking

Each sync operation is tracked with:
- Source system (Confluence/SharePoint)
- Timestamp of sync
- Success/failure status  
- Error details for failures
- Sync control decisions (enabled/disabled by SyncTracker)

### Performance Metrics

- Sync cycle duration
- Items processed per cycle
- API call counts and response times
- Memory usage patterns
- Sync skip counts due to SyncTracker settings

## Future Enhancements

### Planned Improvements

1. **Field-Level Change Tracking**: More granular change detection with hashing
2. **Conflict Resolution UI**: Web interface for manual conflict resolution
3. **Real-Time Sync**: WebHook-based triggers for immediate sync
4. **Batch Processing**: Improved performance for large datasets
5. **Multi-Tenant Support**: Support for multiple SharePoint/Confluence instances
6. **Auto-Page Creation**: Automatic Confluence page creation from SharePoint entries
7. **Template Management**: Dynamic template selection and page provisioning

### Extensibility Points

- **Custom Field Mappers**: Plugin architecture for custom field transformations
- **Custom Sync Rules**: Business logic plugins for conditional sync
- **Additional Platforms**: Framework supports additional integration targets
- **Placeholder Handlers**: Configurable placeholder detection and replacement rules
