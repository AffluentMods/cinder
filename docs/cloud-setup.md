# Cinder cloud connectors — first-time setup

Cinder's cloud connectors (Google Drive, OneDrive, Dropbox) are an **opt-in advanced
feature**. Out of the box they do nothing — to use them you register your own OAuth
"application" with the provider once, then paste a public client ID into Cinder's settings.

## Why you do this and not Cinder

PKCE makes the client ID **public-by-design** — it's not a secret and shipping it in source
would be safe. But registering an OAuth app ties a Google / Microsoft / Dropbox account to the
project (terms of service, quotas, ban risk). Cinder is free OSS and declines that operational
obligation. You stand up your own ten-minute OAuth registration; Cinder just speaks the protocol.

What you'll paste into Cinder is **only the public client ID** (≈40 chars, alphanumeric).
No client secret, no API key — true PKCE. If a provider asks you to copy a client *secret*,
you've picked the wrong OAuth client type. Stop and re-read the steps.

---

## Google Drive

1. Go to https://console.cloud.google.com/ → log in with the account that should own the OAuth app
2. Top bar → **Select a project** → **New project** → name it "Cinder Personal" (or anything) → Create
3. Left sidebar → **APIs & Services** → **Library** → search and enable both:
   - **Google Drive API**
   - **Google Drive Activity API**
4. Left sidebar → **APIs & Services** → **OAuth consent screen** → **External** → Create
   - App name: anything (e.g. "Cinder")
   - User support email: your email
   - Developer contact: your email
   - Scopes (click *Add or Remove Scopes*): add
     `https://www.googleapis.com/auth/drive.metadata.readonly`
     and `https://www.googleapis.com/auth/drive.activity.readonly`
   - Test users: add the Gmail address(es) you want Cinder to image. **Stay in "Testing" mode**;
     don't submit for verification — that's only needed for distributing to other users.
5. Left sidebar → **APIs & Services** → **Credentials** → **Create Credentials** → **OAuth client ID**
   - Application type: **Desktop app**
   - Name: "Cinder Desktop"
   - Click Create
6. A popup shows your **Client ID** (looks like `123456789-abcdef.apps.googleusercontent.com`)
   - Copy it. **Ignore the Client Secret** — Cinder doesn't use it.
7. In Cinder → Settings → Cloud → Google Drive → paste Client ID → Save

When you click **Connect Google Drive** the first time, Google will show a "this app isn't
verified" warning — that's expected for a personal-use OAuth app in Testing mode. Click
**Advanced** → **Go to Cinder (unsafe)**.

---

## Microsoft OneDrive (and SharePoint)

1. Go to https://entra.microsoft.com/ → sign in with the Microsoft account that should own the app
   (personal Outlook account works; or a work/school account if you want to image work tenants)
2. **App registrations** → **New registration**
   - Name: "Cinder"
   - Supported account types:
     - To image **personal OneDrive** accounts: choose
       *"Accounts in any organizational directory and personal Microsoft accounts"*
     - To image only your work tenant: choose *"Accounts in this organizational directory only"*
   - Redirect URI: pick platform **"Mobile and desktop applications"** and add `http://localhost`
   - Click Register
3. On the new app's overview page, copy the **Application (client) ID** (a GUID)
4. Left sidebar → **API permissions** → **Add a permission** → **Microsoft Graph** →
   **Delegated permissions** → check
   - `Files.Read.All`
   - `Sites.Read.All`
   - `offline_access`
   → Add permissions
5. Left sidebar → **Authentication** → check **"Allow public client flows"** → Yes → Save
   - This is the switch that turns on PKCE / no-secret OAuth for desktop clients
6. In Cinder → Settings → Cloud → OneDrive → paste Application (client) ID → Save
   - If you chose "single tenant" in step 2, also paste your Tenant ID (also on the overview page).
   - For multi-tenant / personal accounts, leave Tenant blank (Cinder uses `common`).

---

## Dropbox

1. Go to https://www.dropbox.com/developers/apps → **Create app**
   - Choose **Scoped access**
   - Choose **Full Dropbox** (image everything) or **App folder** (image only a Cinder-named folder)
   - Name: "Cinder Personal" (must be unique across all Dropbox apps — append a number if taken)
   - Click Create
2. On the app's settings page, **Permissions** tab → check
   - `files.metadata.read`
   - `files.content.read` (only if you want Cinder to download bytes for hashing/preview, not just metadata)
   - Click **Submit** at the bottom
3. **Settings** tab → scroll to **OAuth 2** section
   - **Redirect URIs** → add `http://localhost:0/cinder/oauth` and click Add
     (Dropbox accepts any port on localhost; the literal `:0` works because Cinder picks a real port at runtime)
   - **PKCE** → ensure **PKCE** is enabled (it is by default for new apps)
4. At the top of the **Settings** tab, copy the **App key** (≈15 chars)
5. In Cinder → Settings → Cloud → Dropbox → paste App key → Save

---

## Editing the file directly (no UI yet)

Until the Settings → Cloud UI ships (Phase 8.2), edit the JSON directly:

- Windows: `%LOCALAPPDATA%\Cinder\settings.json`
- Linux: `~/.config/Cinder/settings.json`

Add (or extend) the `CloudClientIds` block:

```json
{
  "CloudClientIds": {
    "google-drive": "123456789-abc.apps.googleusercontent.com",
    "onedrive": "00000000-0000-0000-0000-000000000000",
    "dropbox": "abcd1234efgh5678"
  }
}
```

Restart Cinder — you'll see the **Connect** buttons light up under Settings → Cloud.

---

## What if the provider rejects the redirect URI?

When you click **Connect <Provider>**, Cinder spawns a tiny localhost HTTP listener on a random
free port and opens your default browser at the provider's authorization URL. The provider
redirects the browser back to that exact `http://127.0.0.1:<port>/cinder/oauth/callback` URI —
which is why providers need to allowlist localhost.

If the provider returns a "redirect_uri_mismatch" error:

| Provider | Fix |
|---|---|
| Google | Re-confirm the OAuth client type is **Desktop app** (not Web). Desktop type allows any loopback. |
| Microsoft | In Authentication → Platform configurations, ensure `http://localhost` is in the Mobile/desktop redirect list (no port, no path). |
| Dropbox | Add the exact URI Cinder logs (e.g. `http://localhost:54731/cinder/oauth`) to your app's Redirect URIs and Save. |

If it still fails, paste the full URL from the browser's address bar into the Cinder
**Settings → Cloud → Diagnostics** panel — it shows the exact mismatch.

---

## Revoking access

These are your accounts and your OAuth apps. To stop Cinder from accessing them:

- Google: https://myaccount.google.com/connections → click your Cinder app → Remove access
- Microsoft: https://myaccount.microsoft.com/applications-and-services → revoke
- Dropbox: https://www.dropbox.com/account/connected_apps → disconnect

To delete the OAuth app entirely (so even pasting the client ID into another Cinder install
won't work), delete it from the developer console where you created it.
