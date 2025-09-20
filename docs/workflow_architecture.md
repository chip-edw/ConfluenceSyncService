# Workflow Architecture Documentation

## Overview

This document describes the **sequential dependency-based workflow orchestration** that controls task notifications and human acknowledgements in the ConfluenceSyncService. The workflow ensures that tasks are completed in the correct order with proper dependency enforcement.

**Core Principle**: Tasks must be completed in sequential order based on their due dates, with strict dependency blocking to prevent workflow progression until prerequisite tasks are finished.

---

## Workflow Fundamentals

### Task Grouping Model

Tasks are organized into **sequential groups** based on their calculated due dates:

```
Group 1 (Day -20): [Gentle Chaser - PM Ensure Prepared]
    ↓ (ALL must complete before Group 2 can start)
Group 2 (Day -15): [Delivered] 
    ↓ (ALL must complete before Group 3 can start)
Group 3 (Day -14): [Support Functional Review, Support Technical Review]
    ↓ (ALL must complete before Group 4 can start)  
Group 4 (Day -11): [Questions/Comments, Support Attendees]
    ↓ (continues...)
```

### Group Definition

A **task group** is defined by tasks that share the same:
- `AnchorDateType` (GoLive or HypercareEnd)
- `StartOffsetDays` (calculated due date)
- `CustomerId` (workflow is per-customer)

### Parallel vs Sequential Execution

- **Within a group**: Tasks execute **in parallel** (can be worked simultaneously)
- **Between groups**: Tasks execute **sequentially** (strict dependency blocking)

---

## Task Status Management

### SharePoint Status Values

Tasks use the standard SharePoint Choice field with three values:
- **"Not Started"** - Task has not begun
- **"In Progress"** - Task is actively being worked  
- **"Completed"** - Task is finished

### Progression Rules

1. **Sequential Group Progression**: Groups must complete in `StartOffsetDays` order
2. **Dependency Blocking**: Group N+1 cannot start until Group N is 100% complete
3. **Calendar Due Constraint**: Group must also be calendar due to become eligible
4. **Parallel Task Processing**: All tasks within an eligible group can be notified simultaneously

---

## Due Date Calculations

### Anchor Date Resolution

Tasks use one of two anchor date types:

- **GoLive**: Uses `GoLiveDate` from Transition Tracker
- **HypercareEnd**: Uses `HypercareEndDate` from Transition Tracker

### Business Day Calculations

```csharp
// Example: Task with StartOffsetDays = -14, GoLive anchor
var dueDate = BusinessDayHelper.AddBusinessDays(goLiveDate, -14);
```

**Regional Timezone Handling**:
- All calculations performed in **America/Chicago** timezone
- Business days exclude weekends and holidays
- Due dates always calculated as 9:00 AM local time

---

## Workflow Orchestration Logic

### Current Group Identification

The system must identify the **current active group** for each customer:

```csharp
// Pseudo-code for group identification
var incompleteGroups = GetIncompleteTaskGroups(customerId)
    .OrderBy(g => g.StartOffsetDays)
    .ToList();

var currentGroup = incompleteGroups.FirstOrDefault();
if (currentGroup != null && currentGroup.IsCalendarDue())
{
    // This group is eligible for processing
    return currentGroup.Tasks;
}
else
{
    // No eligible groups - either all complete or dependencies not met
    return new List<Task>();
}
```

### Dependency Enforcement

**Strict Blocking Rules**:
1. If any task in Group N is incomplete, Group N+1 cannot start
2. Calendar due date is necessary but not sufficient - dependencies must also be met
3. No "skip ahead" logic - each group must complete fully

### Processing Logic Flow

```
FOR each Customer:
  1. Get all incomplete task groups ordered by StartOffsetDays
  2. Find the earliest incomplete group
  3. Check if that group is calendar due
  4. IF (earliest group is due AND all previous groups complete):
       Process all tasks in that group in parallel
     ELSE:
       Skip all tasks for this customer (dependency blocked)
```

---

## Notification & Chase Management

### Initial Notifications

When a group becomes eligible:
1. **Batch notification** to all tasks in the group simultaneously
2. **Teams messages** posted with acknowledgment links
3. **Chase timers** started based on each task's `DurationBusinessDays`

### Chase Logic

**Individual Task Chasing**:
- Each task has its own chase schedule based on `DurationBusinessDays`
- Chase notifications sent independently within the active group
- Tasks can be completed in any order within the group

**Chase Timing Rules**:
```
Initial Due Date: AnchorDate + StartOffsetDays
First Chase: Due Date + DurationBusinessDays  
Subsequent Chases: Every 24 hours (business days only)
```

### Escalation Scenarios

**Delayed Tasks Within Active Group**:
- Continue chasing overdue tasks within the current group
- Do not advance to next group until ALL tasks complete
- Escalation follows standard chase intervals

**Calendar Due vs Dependency Due**:
- Later groups may become "calendar due" while dependencies are blocked
- System logs these as "dependency blocked" rather than processing
- No notifications sent until dependencies clear

---

## Error Scenarios & Recovery

### Broken Dependencies

**Scenario**: Manual intervention marks a later task complete before earlier group finishes.

**Recovery**:
1. Log warning about dependency violation
2. Continue blocking later groups until proper sequence restores
3. Provide admin interface to reset workflow state if needed

### Missing Anchor Dates

**Scenario**: `GoLiveDate` or `HypercareEndDate` not set for customer.

**Recovery**:
1. Skip all tasks for that customer
2. Log error with customer identification
3. Notification to admin for manual intervention

### Task State Corruption

**Scenario**: SharePoint and SQLite cache become inconsistent.

**Recovery**:
1. SQLite cache serves as source of truth for scheduling
2. SharePoint serves as source of truth for completion status
3. Regular reconciliation process validates consistency

---

## Configuration & Customization

### Workflow Template Integration

Tasks are created from `workflow_template.json` with these key fields:

```json
{
  "Key": "SupportTransitionPacketDeliveredT4Weeks_GentleChaserPmEnsurePrepared",
  "TaskName": "Gentle Chaser - PM Ensure Prepared",
  "AnchorDateType": "GoLive",
  "StartOffsetDays": -20,
  "DurationBusinessDays": 4
}
```

### Regional Customization

**Timezone Offsets** (from `appsettings.json`):
```json
"RegionOffsets": {
  "Offsets": {
    "NA": -5,    // Central Time
    "EMEA": 1,   // Central European Time  
    "APAC": 10   // Australia Eastern Time
  }
}
```

**Business Hours** (configurable per region):
```json
"BusinessWindow": {
  "StartHourLocal": 9,
  "EndHourLocal": 17,
  "CushionHours": 12
}
```



---

## Business Rules Summary

### Core Workflow Constraints

1. **Sequential Execution**: Groups must complete in `StartOffsetDays` order
2. **Complete Before Advance**: 100% group completion required before next group
3. **Calendar + Dependency**: Both conditions must be met for eligibility  
4. **Parallel Within Group**: Tasks in same group can execute simultaneously
5. **Individual Chase Timing**: Each task follows its own `DurationBusinessDays` schedule

### Edge Case Handling

1. **Late Group Completion**: Continue chasing until complete, do not skip ahead
2. **Holiday Delays**: Business day calculations account for regional holidays
3. **Manual Interventions**: System adapts to manual status changes
4. **System Downtime**: Resume from last known state, no lost notifications

This sequential dependency model ensures predictable workflow progression while maintaining flexibility for parallel task execution within appropriate boundaries.