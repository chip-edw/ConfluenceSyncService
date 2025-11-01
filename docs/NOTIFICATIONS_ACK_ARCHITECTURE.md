# Teams Notifications & Acknowledgments (NOTIFICATIONS_ACK_ARCHITECTURE.md)

## Overview
This document explains how the service sends Teams notifications for due tasks, how “Mark complete” links are generated and validated, and how acknowledgments update SharePoint and the local cache.

**Why a separate doc?** The workflow/sync docs describe *when* and *why* we notify; this document shows *how* notifications and ACKs are implemented end-to-end.

---

## Components

### TeamsNotificationService
- **Role:** Creates the initial (root) Teams message and posts “chaser” replies for overdue tasks.
- **Initial Send (root):** Builds HTML (or Adaptive Card) with the first actionable link (ACK) and posts to the target team/channel. Stamps the SP item with `NotifiedAtUtc`.
- **Chasers (replies):** Posts a short overdue note and a minimal card/reply with an **OpenUrl** action back to the ACK link. Updates local chase timestamps/`AckVersion`.

### ChaserService (Hosted background worker)
- **Role:** Decides *when* to notify. Uses group-eligibility rules (calendar-due + dependency gates) to trigger the initial send, then schedules overdue chasers.

### AckActionHandler (HTTP endpoint)
- **Role:** Handles signed ACK links.
- **Flow:**
  1) Validate HMAC signature and expiry.
  2) Resolve the clicker identity (for audit).
  3) Update SharePoint item status → **Completed**.
  4) Update local SQLite cache (TaskIdMap) to keep state consistent.
  5) Return a simple success page.

### SignedLinkGenerator + HMAC Signer
- **Role:** Creates time-bound, tamper-evident URLs.  
- **Notes:** TTL and versioning (`AckVersion`) protect against stale links; keys are stored in the configured secrets provider.

---

## Data Model (SQLite)

### TaskIdMap (key fields)
- **Identity/mapping:** `TaskId`, `CustomerId`, `WorkflowId`, `PhaseName`, `TaskName`, `Category_Key`, `Region`, `AnchorDateType`, `StartOffsetDays`, `DueDateUtc`.
- **Teams threading:** `TeamId`, `ChannelId`, `RootMessageId`, `LastMessageId`.
- **ACK lifecycle:** `AckVersion`, `AckExpiresUtc`, `Status`.
- **Chasing:** `NextChaseAtUtcCached`, `LastChaseAtUtc`.

**Why both SharePoint + SQLite?**  
- SharePoint is the **source of truth** for task completion.  
- SQLite is the **scheduler’s state** (thread ids, chase timers, next actions).

---

## Message Patterns

### Initial Notification (root)
- One root message per task, posted when a group first becomes eligible.
- Contains the first actionable **ACK link** (signed URL).
- Records `RootMessageId` in `TaskIdMap`.

### Chaser Replies (threaded)
- Posted as replies under the root thread:
  1) Short “overdue” text.
  2) Minimal card or HTML link with **“Mark Complete” (OpenUrl)**.
- Updates `LastMessageId` and next-chase window.

---

## Adaptive Card

You can use a richer template for the first message, and a minimal one for chasers.

**Minimal chaser card (example):**
```json
{
  "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
  "type": "AdaptiveCard",
  "version": "1.5",
  "body": [
    { "type": "TextBlock", "size": "Large", "weight": "Bolder", "text": "Task is overdue" },
    { "type": "TextBlock", "wrap": true, "text": "Please acknowledge or complete this task." }
  ],
  "actions": [
    { "type": "Action.OpenUrl", "title": "✅ Mark Complete", "url": "${ackUrl}" }
  ]
}
'''

**Richer template (initial message):** keep in `Data/Templates/TaskNotificationCard.json` with placeholders:
- `title, summary, dueDate, phaseName, priority, assigneeDisplayName, customerName`
- Links: `completionUrl` (ACK), `confluenceUrl`, `sharePointUrl`

---

## Configuration

- **Secrets:** HMAC signing key in your secrets provider (AKV recommended).
- **Public Base URL:** External base URL used to build ACK links.
- **Teams Target:** Team/Channel ids; consider config per region or per customer segment.
- **Chase Policy:** Duration business days, windows, and escalation rhythm.

---

## Security & Idempotency

- **Signed URLs:** HMAC + TTL + `AckVersion` guard against replay and stale links.
- **Idempotent ACKs:** Double-click safe—subsequent hits verify current state and exit.
- **Auditability:** Persist `AckedBy`, `AckTimestampUtc` (if available) on completion.

---

## Troubleshooting
- “Link opens but status doesn’t change” → verify signature TTL, key sync, and that the service’s public URL matches the link’s host.
- “Duplicate messages” → confirm root send stamping and that `RootMessageId` is stored; verify the scheduler’s “already notified” guard.
- “Chasers not threading” → ensure `RootMessageId` is present before replies; confirm channel permissions and app auth scopes.
