# 🛣️ ConfluenceSyncService Roadmap

## ✅ Completed Features

### 🔄 Sync & Orchestration Foundation
- [x] Bi-directional sync foundation between **Microsoft SharePoint Lists** and **Atlassian Confluence Cloud**
- [x] `SharePointClient` and `ConfluenceClient` scaffolding complete
- [x] `SyncOrchestratorService` initialized and operational
- [x] OAuth2 flow for Confluence Cloud using **refresh token**, `access_token`, `cloudId`
- [x] `SqliteSecretsProvider` reads/writes config from `ConfigStore` SQLite table
- [x] `ConfluenceAuthClient` and `ConfluenceTokenManager` with token caching and refresh
- [x] Strongly-typed DTOs (`ConfluenceTokenResponse`, `AtlassianAccessibleResource`)
- [x] Token expiration logic encapsulated via `IsExpired()` in `ConfluenceTokenInfo`
- [x] Thread-safe token cache via `ConcurrentDictionary`

### 🎯 Sequential Workflow Dependency System
- [x] **Category_Key field** added to TaskIdMap table with proper indexing
- [x] **CategoryOrderProvider service** for template-based category ordering from Workflow_template.json
- [x] **Sequential workflow dependency filtering** (`ApplyWorkflowDependencyFilterAsync`)
  - Category-level progression enforcement
  - Parallel task support within same category/offset group
  - Strict metadata validation (CustomerId, PhaseName, CategoryKey, AnchorDateType, StartOffsetDays)
- [x] **Cross-anchor dependency blocking** (HypercareEnd waits for GoLive completion via `IsAnchorTypeCompleteAsync`)
- [x] **Category completion checking** (`IsCategoryCompleteAsync`) per customer/phase/anchor
- [x] **Earliest offset detection** (`GetEarliestOpenOffsetAsync`) for task-level gating
- [x] **AnchorDateType scoping** - separate workflow tracks per anchor type
- [x] Schema upgrader handles Category_Key column migration

### 💾 Cache Synchronization & Data Quality
- [x] **SQLite cache sync on ACK** - `AckActionHandler` updates both SharePoint and SQLite Status field
- [x] **Dual-path cache healing**:
  - ACK handler immediate sync
  - Chaser discovery sync (line 133 fallback)
- [x] **Status field tracking** to prevent stale gating logic
- [x] **Cache invalidation bug fix** (2025-10-26) - resolved category advancement issue

### 🛠️ Enhanced Self-Healing System
- [x] **NULL Category_Key backfill** from Workflow_template.json via TaskName lookup
- [x] **AnchorDateType persistence** and propagation from template
- [x] **Template change propagation** to existing tasks
- [x] **NextChaseAtUtcCached recalculation** when anchor/offset changes
- [x] **DueDateUtc calculation and population** in UpsertPhaseTasksAsync
- [x] **Completed task protection** - never updates completed tasks during healing
- [x] **Dry run mode support** for safe testing of date/metadata updates
- [x] Enhanced self-healing in `EnhancedSelfHealingWithWorkflowFields` method

### 📅 Due Date Management
- [x] **DueDateUtc calculation** using `CalculateTaskDueDate` helper (anchor + offset)
- [x] **NULL DueDateUtc skip check** in ChaserJobHostedService
- [x] **Template-based due date calculation** respecting GoLive and HypercareEnd anchors
- [x] **Dry run support** for DueDateUtc writes (logs without SharePoint updates)
- [x] Proper SharePoint field population during task creation

### 📢 Teams Notification Enhancements
- [x] **Context-rich notifications** with CompanyName, DueDateUtc, PhaseName
- [x] **Message threading** with RootMessageId and LastMessageId tracking
- [x] **Enhanced notification cards** with formatted context display
- [x] **ACK link expiration** tied to next chase schedule
- [x] **Thread continuity** - replies persist to conversation root
- [x] `PostChaserWithIdsAsync` method for message ID capture
- [x] SQLite persistence of message IDs for threading

### ⚡ Performance Optimizations
- [x] **CustomerId column** added to TableSyncStates table
- [x] **Fast CustomerId-based queries** replacing slow CustomerName text searches
- [x] **Eliminated unnecessary SharePoint API calls** via CustomerId lookup optimization
- [x] **Schema indexes** on Category_Key and CustomerId for query performance
- [x] Composite indexes: `IX_TaskIdMap_CustomerId_Category_Key_AnchorDateType`

### 🗄️ Database Schema Enhancements
- [x] **Category_Key column** in TaskIdMap with indexes
- [x] **CustomerId column** in TableSyncStates
- [x] **RootMessageId and LastMessageId** for Teams threading
- [x] **AckVersion, AckExpiresUtc, LastChaseAtUtc** for ACK link rotation
- [x] **StartOffsetDays column** for workflow offset tracking
- [x] **Schema upgrader** with version tracking and migration logic
- [x] **SqliteSchemaUpgrader** automatic column and index creation

### 🔐 Security & Validation
- [x] **HMAC signature verification** for ACK links (id + exp + optional correlation)
- [x] **TTL enforcement** for ACK link expiration
- [x] **Idempotent ACK processing** tolerates already-completed tasks
- [x] **Parametrized SQL queries** throughout for injection prevention

### 🔧 Developer Experience
- [x] **DryRun mode** for ChaserJob (safe testing without Teams/SharePoint writes)
- [x] **Comprehensive logging** with structured data (Serilog)
- [x] **Debug logging** for candidate filtering and workflow decisions
- [x] **Connection string parsing** with fallback to packaged DB
- [x] **Graceful error handling** in ACK handler (never 500s the user)

---

## 🧩 Planned / Backlog

### 🎯 High Priority

#### 📊 Cache & Data Quality Improvements
- [ ] **TaskStatus constants**: Replace magic strings ("Completed", "Not Started", "In Progress") with centralized constants to prevent typos and ensure consistency across codebase
  - Create `TaskStatus` static class with const fields
  - Refactor all status comparisons to use constants
  - Update SQL queries and SharePoint integrations
  - **Impact**: Prevents subtle bugs from status string mismatches

- [ ] **Enhanced cache sync on ACK**: Extend ACK handler to update all audit fields in SQLite cache
  - Add `CompletedUtc`, `AckedBy`, `AckedEmail` to cache update
  - Improves audit trail and data quality
  - **Current**: Only syncs `Status` field (sufficient for gating, but incomplete for reporting)

### 🔒 Security Enhancements
- [ ] **Sign ACK link `list` parameter**: Include `listId` in HMAC signature to prevent list ID tampering
  - Update `BuildAckUrl()` to include list in signed payload
  - Update `AckActionHandler` verification to validate list parameter
  - **Priority**: Low (only needed if multi-list ACK links are generated)

### 🛠️ Enhanced Self-Healing Configuration
- [ ] **Template propagation configuration flag**: Add `WorkflowHealing.EnableTemplatePropagation` setting to allow disabling template field updates during self-healing for emergency scenarios or debugging

### 🧰 Template → TaskIdMap Reconcile (Maintenance Job)
- [ ] **Feature flag**: Add `Maintenance.ReconcileTemplate.Enabled` to toggle the job on/off
- [ ] **Dry run**: Add `Maintenance.ReconcileTemplate.DryRun` to log planned updates only
- [ ] **Scope**: Target tasks with `State='linked'` and not `Status='Completed'`
- [ ] **Updates**: Propagate `AnchorDateType` and `StartOffsetDays`; recompute `NextChaseAtUtcCached` when anchor/offset changed or null
- [ ] **Cadence**: `Maintenance.ReconcileTemplate.CadenceMinutes` (default 60)
- [ ] **Minimal logging**: Per-run summary counts; warn if anchor dates (`GoLive`/`HypercareEnd`) missing

### 🔐 Security & Configuration
- [ ] **ProfileValidator**: Ensures all required keys (`ClientId`, `ClientSecret`, `RefreshToken`) exist before running a sync for a given profile

### 🔄 Sync Features
- [ ] **Full SharePoint → Confluence sync path validation**
- [ ] **Confluence → SharePoint sync implementation**
- [ ] **SyncMapper**: Bi-directional field mapping between SharePoint List fields and Confluence content (planned, not started)

### 🚀 Performance & Optimization
- [ ] **Field-level change detection with hashing**: Implement hash-based change detection for Confluence HTML table fields to only sync modified fields to SharePoint, reducing API calls and improving performance
- [ ] **Batch API operations**: Implement Microsoft Graph batch requests for multiple SharePoint updates
- [ ] **Delta queries**: Use SharePoint delta queries to only retrieve changed items
- [ ] **Caching layer**: Add Redis/in-memory caching for frequently accessed field mappings

### 📦 Secrets & Profile Management
- [ ] **Profile-based configuration**: Load config by `profileKey` to support multi-tenant setups
- [ ] **Azure Key Vault Provider**: Optional secrets provider to replace SQLite in production

### 🛠 Admin/Diagnostic Utilities
- [ ] **Profile diagnostic endpoint**: List available profiles, show missing fields, last sync status
- [ ] **Token refresh history logging** for diagnostics and support

### 🧪 Testing & Reliability
- [ ] Unit tests for `ConfluenceAuthClient`, `TokenManager`, and secrets providers
- [ ] Retry policies for failed token or sync operations
- [ ] Integration tests for sequential workflow gating logic
- [ ] End-to-end ACK flow testing (SharePoint update → SQLite sync → next task eligibility)
- [ ] Automated regression tests for workflow dependency scenarios

---

## 📝 Architecture & Design Notes

### Sequential Workflow Gating Design
**Grouping scope**: `(CustomerId, PhaseName, AnchorDateType)`
- Each customer/phase has independent workflows per anchor type
- GoLive tasks complete before HypercareEnd tasks can start
- Within each anchor type, categories progress sequentially
- Tasks at same category/offset can execute in parallel

**Category progression**:
1. Find earliest incomplete category for scope
2. Find earliest incomplete offset within that category
3. Eligible set = all tasks at (category, offset) that are due
4. Only advance category when ALL tasks in category complete

### Cache Sync Strategy
**Dual-path healing for resilience**:
1. **Primary**: ACK handler immediate sync (user-triggered)
2. **Fallback**: Chaser discovery sync (scheduled, line 133)

This ensures cache eventually heals even if ACK handler fails.

### Recent Fixes & Learnings
- **Cache invalidation bug (2025-10-26)**: Fixed issue where ACK handler only updated SharePoint but not SQLite cache, causing workflow gating to see stale "Not Started" status and incorrectly unlock all downstream tasks. Solution: Added cache sync in `AckActionHandler` after successful SharePoint update.
- **Redundant cache sync paths**: System has two cache sync mechanisms (ACK handler immediate sync + chaser discovery sync), providing resilient self-healing even if one path fails.
- **DueDateUtc population**: Initially missing from task creation; fixed by adding calculation to `UpsertPhaseTasksAsync` with dry run support.
- **Category_Key backfill**: 25 tasks had NULL Category_Key due to self-healing gaps; resolved via enhanced template lookup logic.