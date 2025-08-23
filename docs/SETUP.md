# SETUP.md

# Developer Setup Instructions

This document describes how to configure your local development environment to begin building and testing the SharePoint-Confluence Middleware Sync Engine.
It includes setup for Microsoft Graph and Atlassian Confluence Cloud OAuth 2.0 authentication.

---

## 1. Prerequisites

- Visual Studio 2022 or higher (.NET 8 SDK installed)
- GitHub account and repository access
- Access to:
  - **Microsoft 365 Developer Tenant** (D365 developer license active)
  - **Confluence Cloud Free Plan (10-user tier)**

---

## 2. Confluence Cloud OAuth 2.0 Setup (One-Time Authorization Flow)

This app uses Atlassian's **OAuth 2.0 Authorization Code (3LO)** flow to access the Confluence Cloud REST API v1. A one-time setup is required to authorize the integration user and obtain a long-lived `refresh_token`.

### Step 1: Register Your App

1. Go to: [Atlassian Developer Console](https://developer.atlassian.com/console/myapps)
2. Create a new app using **OAuth 2.0 (3LO)**
3. Set the following:
   - **App Name**: `ConfluenceSyncService`
   - **Redirect URL**: `http://localhost:5000/callback`
   - **Scopes**:
     - `read:confluence-content.all`
     - `write:confluence-content`
     - `read:confluence-space.summary`
     - `offline_access` ✅ (required for refresh_token support)
4. Save the **Client ID** and **Client Secret**

### Step 2: Authorize the App (One-Time User Consent)

Paste this URL into your browser (replace placeholders with your values):

```
https://auth.atlassian.com/authorize?audience=api.atlassian.com&client_id=YOUR_CLIENT_ID&scope=read%3Aconfluence-content.all%20write%3Aconfluence-content%20read%3Aconfluence-space.summary%20offline_access&redirect_uri=http://localhost:5000/callback&response_type=code&prompt=consent
```

1. Log in with your Confluence integration user
2. Approve access
3. Copy the `code` from the redirected URL:
   ```
   http://localhost:5000/callback?code=ABC123...
   ```

### Step 3: Exchange the Code for Tokens

Use Postman, curl, or a small .NET console app to POST to:

```
POST https://auth.atlassian.com/oauth/token
Content-Type: application/json
```

With body:

```json
{
  "grant_type": "authorization_code",
  "client_id": "YOUR_CLIENT_ID",
  "client_secret": "YOUR_CLIENT_SECRET",
  "code": "CODE_FROM_REDIRECT",
  "redirect_uri": "http://localhost:5000/callback"
}
```

Response:

```json
{
  "access_token": "abc...",
  "expires_in": 3600,
  "refresh_token": "def...",
  "scope": "..."
}
```

✅ Store the `refresh_token` securely for long-term use.

### Step 4: Update `appsettings.json`

```json
"ConfluenceOAuth": {
  "Profiles": {
    "Default": {
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret",
      "RefreshToken": "your-refresh-token"
    }
  }
}
```

---

## 3. Microsoft Graph API Setup

### Step 1: Register the Application

1. Go to the Entra Admin Center: [https://entra.microsoft.com](https://entra.microsoft.com)
2. Navigate to **Applications → App registrations**
3. Click **+ New registration**
4. Fill out the form:
   - **Name**: `ConfluenceSyncService`
   - **Supported account types**: **Accounts in this organizational directory only**
   - **Redirect URI**: Leave blank (not needed)
5. Click **Register**

### Step 2: Record Credentials

Copy and store:

- **Application (client) ID**
- **Directory (tenant) ID**

### Step 3: Create a Client Secret

1. Go to **Certificates & secrets**
2. Click **+ New client secret**
3. Store the generated secret value

### Step 4: Grant API Permissions

1. Go to **API permissions** → **+ Add a permission**
2. Add:
   - `Sites.ReadWrite.All`
   - `Group.ReadWrite.All`
3. Click **Grant admin consent**

---

## 4. Repository Structure

```
/Confluence_Sync_Service
  /Confluence_Sync_Service
    /Services
    /Models
    /Clients
    /Auth
    Program.cs
    Worker.cs
  /docs
    README.md
    SETUP.md
  Confluence_Sync_Service.sln
```

---

## 5. Next Steps

- Finalize Confluence and SharePoint client logic
- Wire up token management with `ConfluenceAuthClient`
- Implement the sync orchestration logic
- Add sync interval, status logging, and manual trigger API
- Extend for bi-directional sync support

---

## Phase 1 API Endpoint Map

### Confluence API (REST v1)

- `GET /wiki/api/v1/contents/database` — List databases
- `GET /wiki/api/v1/contents/database/{id}/item` — List items
- `POST /wiki/api/v1/contents/database/{id}/item` — Create item

### SharePoint (MS Graph API)

- `GET /sites/{site-id}/lists` — List SharePoint lists
- `GET /sites/{site-id}/lists/{list-id}/items` — Get items
- `POST /sites/{site-id}/lists/{list-id}/items` — Create item

---

**NOTE:** This app is designed to run on the Confluence Free tier. Avoid using features restricted to Premium unless guarded behind feature toggles.


---

## Appendix A (2025-08-23): Workflow Notifications & Identity (Dev → Prod)

This section augments the original setup with the current workflow/notifications work so dev translates cleanly to prod.

### A1. Current project configuration (already implemented)
- **Mappings**: `mapping.transition.json` via `WorkflowMappingProvider` with keys: `transitionTracker`, `transitionCustomers`, `phaseTasks`, `transitionResources`.
- **Cursor store**: `%LOCALAPPDATA%/ConfluenceSyncService/cursors.json` with key `Cursor:TransitionTracker:lastModifiedUtc`.
- **SharePoint Lists** in scope:
  - Transition Tracker
  - TransitionCustomers
  - Phase Tasks & Metadata
  - Transition Resources
- **Fields**: writes use `SharePointFieldMappings` (avoid read-only fields like `LinkTitle`, `Modified`).

### A2. Teams channel configuration (appsettings)
Add (or confirm) the Teams settings:
```json
"Teams": {
  "Team": "Support Transition Checklists",
  "TeamId": "00000000-0000-0000-0000-000000000000",
  "Channel": "Task Notifications",
  "ChannelId": "19:xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx@thread.tacv2"
}
```

**Graph permissions** (app registration) to support channel posting:
- `Group.ReadWrite.All`
- `ChannelMessage.Send` (if using application permissions/RSC for channel posts)

> If you post as the app into a team, ensure the app is installed in that team and consent is granted as required by Microsoft Graph.

### A3. Notifications config (appsettings additions)
```json
"PublicBaseUrl": "https://<public-host>",
"Notifications": {
  "MarkCompleteLinkTtlMinutes": 120,
  "DefaultFallbackEmail": "me@example.com"
}
```
> `PublicBaseUrl` is where the one-click endpoint is reachable (dev can be Azure App Service Free or VS Dev Tunnel).

### A4. One-click ACK endpoint (Management API)
- **Route**: `GET /maintenance/actions/mark-complete`
- **Query params** (all signed): `cid` (CustomerId), `pid` (PhaseId), `corr` (CorrelationId), `actor` (intended assignee email), `exp` (UTC expiry)
- **Signature**: HMAC via `SignatureService` over canonical string `cid|pid|corr|actor|exp`
- **Behavior**: verify signature+TTL → idempotently set `Status="Completed"`, `CompletedDate=UtcNow`, `AckedBy=actor` → small success page
- **Optional**: if `IdentityMode` (below) provides an authenticated user, also stamp `AckedByActual=<UPN/email>`

### A5. Identity modes (portable dev ⇄ prod)
Add a config switch and read identity accordingly:
```json
"Identity": {
  "Mode": "EasyAuth" // or "JwtBearer" or "HeaderSso"
}
```
- **EasyAuth (Azure App Service Authentication)**: read `X-MS-CLIENT-PRINCIPAL-NAME` (and/or decode `X-MS-CLIENT-PRINCIPAL`) to identify who clicked.
- **JwtBearer (Microsoft.Identity.Web)**: protect the ACK endpoint with Entra ID; read `preferred_username`/`upn`/`emails` from token claims.
- **HeaderSso**: trust a specific header (e.g., from a reverse proxy/App Proxy) like `X-Authenticated-User`.

> For dev with **Azure App Service (Easy Auth)** you get silent SSO for most users and the same code path works in prod if you also host on App Service.

### A6. Dev setups (choose one)
**Option 1 — Azure App Service (Free) with Easy Auth**
1. Publish the Management API to an App Service (Free tier OK).
2. In **Authentication**, add Microsoft provider and require authentication.
3. In code, read `X-MS-CLIENT-PRINCIPAL-NAME` to stamp `AckedByActual`.
4. Set `Identity.Mode = "EasyAuth"` and `PublicBaseUrl` to the App Service URL.

**Option 2 — Local with JwtBearer + VS Dev Tunnels**
1. Add Entra ID OIDC to the API (JwtBearer) and add your tunnel URL as a redirect.
2. Start a **Dev Tunnel** in Visual Studio for external HTTPS.
3. Set `Identity.Mode = "JwtBearer"` and `PublicBaseUrl` to the tunnel URL.

### A7. Teams message format (MVP)
- Post to the configured channel with:
  - The task summary (Customer/Phase/TaskName)
  - **@mention** of the assigned resource email (real mention when AAD id is resolved; plain text if missing)
  - One-click link: `${PublicBaseUrl}/maintenance/actions/mark-complete?...&sig=...`
- When past due, mark the message as **Important** (Teams flag).

### A8. Chasers / breach notices (placeholder)
- Store `NotifiedAtUtc`, `ChaseCount`, `NextChaseAtUtc` in **Phase Tasks & Metadata**.
- A background pass resends if `Status != Completed` and `Now ≥ NextChaseAtUtc`.
- Breach notices use Teams **Important** flag. Frequency/limits to be finalized.

### A9. Transition Assignments (per-customer roles) — planned
- **Confluence dashboard** per customer with a simple HTML table (`Phase`, project side: PM/Functional/Technical emails; support side: PM/Functional/Technical emails).
- **SharePoint list** `Transition Assignments`: 1 row per `CustomerId + PhaseID + Side + Role` with `ResourceEmail` (+ optional `AADUserId` cache).
- Notification resolver uses this list to pick the assignee; if missing, post without mention and log a warning.


---

## Appendix B (2025-08-23): Production Setup (delta)

This section lists the additional steps and environment-specific differences to take this solution to production. It **does not** replace earlier dev steps; it builds on them so the dev → prod move is predictable.

### B1. Choose a production hosting model
- **Option 1 (Recommended): Azure App Service + Easy Auth**
  - Easiest 1:1 with the dev guidance; identity comes from injected headers.
- **Option 2: VM behind Microsoft Entra ID Application Proxy**
  - Good for internal-only hosting; prefer in-app OpenID Connect for identity.

> Pick one and stick with it per environment (Dev/Test/Prod) for consistency.

### B2. Azure App Service (Easy Auth) — production steps
1. **Create App Service (Windows/Linux)** on the target subscription/resource group.
2. Enable **Authentication** → **Add identity provider** → **Microsoft** → complete setup.
3. In **Authentication** → **Settings**: set **Require authentication** = **On**.
4. In **Configuration** (App Settings), set:
   - `Identity:Mode = EasyAuth`
   - `PublicBaseUrl = https://<your-prod-host>`
   - `ASPNETCORE_ENVIRONMENT = Production`
   - Key Vault references for secrets (see B5).
5. In **Custom domains**: bind `https://<your-prod-host>` + TLS cert; force **HTTPS only**.
6. In **App Insights**: enable and capture logs/traces/requests (see B7).
7. **Teams links**: ensure the posted “Mark complete” link uses `<your-prod-host>`.
8. **Outgoing identity headers** to your app (no code changes required):
   - Read `X-MS-CLIENT-PRINCIPAL-NAME` (UPN/email) and/or decode `X-MS-CLIENT-PRINCIPAL` to stamp who clicked.

### B3. VM + Entra ID Application Proxy — production steps
1. **Install the Application Proxy connector** on a Windows Server that can reach your API.
2. **Publish the app** in Entra admin center → **Enterprise applications** → **New application** → **On-premises application**:
   - Internal URL: your API (e.g., `http://vmname:5000/`)
   - Pre-authentication: **Azure Active Directory**
   - Assign the user groups who can access.
3. **Identity** (choose one):
   - **Preferred**: set `Identity:Mode = JwtBearer` and protect the ACK endpoint with Microsoft Entra (OpenID Connect in-app). Read claims (`preferred_username`/`upn`/`emails`) to stamp who clicked.
   - **Advanced**: if a trusted reverse proxy injects a header (e.g., `X-Authenticated-User`), set `Identity:Mode = HeaderSso` and read that header. (Ensure requests can only arrive via that proxy.)
4. **Lock down inbound** to only the proxy’s egress and enforce HTTPS on the VM.
5. **Teams links**: use the App Proxy external URL in `PublicBaseUrl`.

### B4. Microsoft Graph consent in the **prod tenant**
- Ensure the app registration in prod has (application) permissions:
  - `Group.ReadWrite.All`
  - `ChannelMessage.Send` (if used for posting into channels)
- Grant **Admin consent** and, if required, install the app in the target team.
- Update app settings with the **prod** `TeamId` & `ChannelId`.

### B5. Secrets & configuration
- Use **Azure Key Vault**; reference secrets in App Service via Key Vault references, or inject via environment variables on the VM.
- Move any client secrets/refresh tokens out of files. Examples:
  - `ConfluenceOAuth:Profiles:Default:ClientSecret`
  - `ConfluenceOAuth:Profiles:Default:RefreshToken`
  - Any Graph client secrets
- Keep environment-specific values in `appsettings.Production.json` or environment variables.

**Sample `appsettings.Production.json`**
```json
{
  "PublicBaseUrl": "https://prod.example.com",
  "Identity": { "Mode": "EasyAuth" },
  "Notifications": {
    "MarkCompleteLinkTtlMinutes": 120
  },
  "Teams": {
    "Team": "Support Transition Checklists",
    "TeamId": "<prod-team-guid>",
    "Channel": "Task Notifications",
    "ChannelId": "<prod-channel-id>"
  }
}
```

### B6. DNS/TLS
- Create public DNS record for `<your-prod-host>`.
- Use a managed certificate (App Service) or upload your own; renew before expiry.
- Enforce TLS 1.2+ and HTTP → HTTPS redirects.

### B7. Monitoring, logs, and alerting
- **Application Insights**: enable and add dashboards for:
  - Request count, latency, failure rate
  - Custom traces for: `notify`, `link hit`, `completed`, `next notified`
- **Alerts**: set alerts on error rate spikes and ACK endpoint 5xx.
- **Retention**: align with compliance (e.g., 90 days) and export to Log Analytics if needed.

### B8. RBAC & least privilege
- Restrict who can trigger manual actions or replays (if exposed).
- Scope Graph permissions to the minimal set; consider team-scoped posting via RSC if applicable.
- Lock production Key Vault to the app’s managed identity only.

### B9. Backup/restore
- Back up **mapping files** and **cursor store** location; document restore steps.
- Export App Service configuration (ARM/Bicep) or VM scripts for reproducible infra.

### B10. Go-live checklist
- [ ] `PublicBaseUrl` points to prod hostname
- [ ] Identity mode set correctly (`EasyAuth`, `JwtBearer`, or `HeaderSso`)
- [ ] Teams `TeamId`/`ChannelId` are prod values; test a sample post
- [ ] ACK endpoint verified end-to-end:
  - Signed link accepted
  - `Status` set to `Completed`
  - `CompletedDate` and `AckedBy` stamped
  - If identity present, `AckedByActual` stamped
- [ ] App Insights receiving traces; alerts configured
- [ ] Secrets in Key Vault; no secrets in files
- [ ] TLS/redirects/DNS validated
- [ ] Runbook for chasers/breach notices in place (frequency/severity)

