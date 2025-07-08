
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
