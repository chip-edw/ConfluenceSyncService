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

### 🔐 Security & Configuration
- [ ] **ProfileValidator**: Ensures all required keys (`ClientId`, `ClientSecret`, `RefreshToken`) exist before running a sync for a given profile

### 🔄 Sync Features
- [ ] **Full SharePoint → Confluence sync path validation**
- [ ] **Confluence → SharePoint sync implementation**
- [ ] **SyncMapper**: Bi-directional field mapping between SharePoint List fields and Confluence content (planned, not started)

### 📦 Secrets & Profile Management
- [ ] **Profile-based configuration**: Load config by `profileKey` to support multi-tenant setups
- [ ] **Azure Key Vault Provider**: Optional secrets provider to replace SQLite in production

### 🛠 Admin/Diagnostic Utilities
- [ ] **Profile diagnostic endpoint**: List available profiles, show missing fields, last sync status
- [ ] **Token refresh history logging** for diagnostics and support

### 🧪 Testing & Reliability
- [ ] Unit tests for `ConfluenceAuthClient`, `TokenManager`, and secrets providers
- [ ] Retry policies for failed token or sync operations