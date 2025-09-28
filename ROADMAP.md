# 🛣️ ConfluenceSyncService Roadmap

## ✅ Completed / In Progress
- [x] Bi-directional sync foundation between **Microsoft SharePoint Lists** and **Atlassian Confluence Cloud**
- [x] `SharePointClient` and `ConfluenceClient` scaffolding complete
- [x] `SyncOrchestratorService` initialized
- [x] OAuth2 flow for Confluence Cloud using **refresh token**, `access_token`, `cloudId`
- [x] `SqliteSecretsProvider` reads/writes config from `ConfigStore` SQLite table
- [x] `ConfluenceAuthClient` and `ConfluenceTokenManager` with token caching and refresh
- [x] Strongly-typed DTOs (`ConfluenceTokenResponse`, `AtlassianAccessibleResource`)
- [x] Token expiration logic encapsulated via `IsExpired()` in `ConfluenceTokenInfo`
- [x] Thread-safe token cache via `ConcurrentDictionary`

## 🧩 Planned / Backlog

### 🛠️ Enhanced Self-Healing Configuration
- [ ] **Template propagation configuration flag**: Add `WorkflowHealing.EnableTemplatePropagation` setting to allow disabling template field updates during self-healing for emergency scenarios or debugging

### 🧰 Template → TaskIdMap Reconcile (Maintenance)
- [ ] **Feature flag**: Add Maintenance.ReconcileTemplate.Enabled to toggle the job on/off.
- [ ] **Dry run**: Add Maintenance.ReconcileTemplate.DryRun to log planned updates only.
- [ ] **Scope**: Target tasks with State='linked' and not Status='Completed'.
- [ ]  **Updates**: Propagate AnchorDateType and StartOffsetDays; recompute NextChaseAtUtcCached when anchor/offset changed or null.
- [ ]  **Cadence**: Maintenance.ReconcileTemplate.CadenceMinutes (default 60).
- [ ]  **Minimal logging**: Per-run summary counts; warn if anchor dates (GoLive/HypercareEnd) missing.

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