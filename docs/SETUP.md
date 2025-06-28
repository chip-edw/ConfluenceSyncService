# SETUP.md

# Developer Setup Instructions

This document describes how to configure your local development environment to begin building and testing the SharePoint-Confluence Middleware Sync Engine.
There are also some basic instructions for helping you create and configure the required Registered Application that is used to authenticate and connect to MSGraph so you can sync Sharepoint Lists with Confluence lists and also send Teams notices and reminders.

---

## 1. Prerequisites

- Visual Studio 2022 or higher (.NET 8 SDK installed)
- GitHub account and repository access
- Access to:
  - **Microsoft 365 Developer Tenant** (D365 developer license active)
  - **Confluence Cloud Free Plan (10-user tier)**

---

## 2. Confluence Cloud API Token Setup

1. Log into Confluence Cloud under your dedicated **integration user** account.
2. Navigate to: `https://id.atlassian.com/manage-profile/security/api-tokens`
3. Select **Create API Token with Scopes**
4. Use the following name for your token:

```
SyncService-Confluence-Dev-20240624
```

5. Select these scopes:

- `read:confluence-content.all`
- `write:confluence-content`
- `read:confluence-space.summary`
- `read:confluence-user`
- `read:database:confluence`
- `write:database:confluence`

**Note:** As of this version, `read:confluence-database.content` and `write:confluence-database.content` scopes are not visible in the API Token UI under Free plan. Database content operations will be accessed via `write:confluence-content`.

6. Generate the token and securely store it.

---

## 3. Microsoft Graph API Setup

Follow these steps to register and configure a Microsoft Entra ID Registered Application for secure middleware access to Microsoft Graph:

### Step 1: Register the Application

1. Go to the Entra Admin Center: [https://entra.microsoft.com](https://entra.microsoft.com)
2. Navigate to **Applications → App registrations**
3. Click **+ New registration**
4. Fill out the form:
   - **Name**: `ConfluenceSyncService` or `SyncServiceMiddleware`
   - **Supported account types**: Select **Accounts in this organizational directory only**
   - **Redirect URI**: Leave blank (not needed for headless service-to-service auth)
5. Click **Register**

### Step 2: Record Core Application Credentials

From the app overview page, copy and store:

- **Application (client) ID**
- **Directory (tenant) ID**

These will be needed in your appsettings.json or environment variables.

### Step 3: Generate a Client Secret

1. Go to **Certificates & secrets** in the left navigation
2. Click **+ New client secret**
3. Set:
   - **Description**: `MainAccessSecret`
   - **Expiration**: 6 or 12 months (rotate periodically in production)
4. Click **Add**
5. ⚠️ **Copy and store the secret value immediately** — it will not be visible again

### Step 4: Add Microsoft Graph Permissions

1. Go to **API permissions** → **+ Add a permission**
2. Choose:
   - **Microsoft Graph**
   - **Application permissions**
3. Add the following:
   - `Sites.ReadWrite.All` — for full access to SharePoint Lists
   - `Group.ReadWrite.All` — for posting to Teams channels
4. Click **Add permissions**
5. Click **Grant admin consent** for the tenant

### Step 5: Final Credential Set

Securely store the following for use in your .NET middleware:

- `ClientId`
- `TenantId`
- `ClientSecret`
- Confirmed permissions: `Sites.ReadWrite.All`, `Group.ReadWrite.All`

These will be used by the Microsoft Authentication Library (MSAL) to obtain access tokens for Microsoft Graph.

---

## 4. Repository Structure

```
/Confluence_Sync_Service
  /Confluence_Sync_Service         <-- Main .NET 8 Worker Service project
    /Services
    /Models
    /Clients
    Program.cs
    Worker.cs
  /docs
    README.md
    SETUP.md
  Confluence_Sync_Service.sln
```

---

## 5. Next Steps

- Build the core Confluence client (REST API v2)
- Build SharePoint client (Microsoft Graph SDK)
- Implement sync orchestration engine
- Implement conflict resolution ruleset
- Implement initial Teams alert connector
- Implement Kestrel-hosted internal API endpoints (health checks, job status, manual triggers)

---

## Phase 1 API Endpoint Map

### Confluence API (REST v2)

- `GET /wiki/api/v2/databases` — List available databases
- `GET /wiki/api/v2/databases/{id}` — Get database structure
- `GET /wiki/api/v2/databases/{id}/items` — Read database rows (content)
- `POST /wiki/api/v2/databases/{id}/items` — Create new row in database
- `PUT /wiki/api/v2/databases/{id}/items/{itemId}` — Update database row

### SharePoint (MS Graph API)

- `GET /sites/{site-id}/lists` — List all SharePoint lists
- `GET /sites/{site-id}/lists/{list-id}/items` — Get SharePoint list items
- `POST /sites/{site-id}/lists/{list-id}/items` — Create SharePoint list item
- `PATCH /sites/{site-id}/lists/{list-id}/items/{item-id}` — Update SharePoint list item

### Microsoft Teams Alerts (optional phase)

- `POST /teams/{team-id}/channels/{channel-id}/messages` — Post alert to Teams

---

**NOTE:** This repository is designed to remain compatible with the Confluence Free tier (non-Premium). All development should assume minimal feature dependencies.