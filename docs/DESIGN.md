# DbxProvider — Design Document

**Status:** Approved  
**Date:** 2026-04-15

---

## 1. Overview

DbxProvider is a C# binary PowerShell module that implements a `NavigationCmdletProvider` to mount a Dropbox account as a PowerShell drive (`Dbx:\`). Users navigate, read, write, copy, move, and delete files and folders using standard PowerShell cmdlets. All Dropbox API communication uses direct `HttpClient` calls (no Dropbox SDK dependency).

## 2. Key Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Scope | File operations only | Sharing, teams, contacts out of scope |
| Technology | C# binary module | Required for full NavigationCmdletProvider support |
| Target | .NET 10, PowerShell 7+ | Latest platform |
| HTTP Client | Direct `HttpClient` | No SDK dependency, full control, lightweight |
| Authentication | OAuth 2.0 PKCE | Most secure and user-friendly for desktop tools |
| Token Storage | Windows Credential Manager | OS-level encryption, per-user |
| App Key | User-supplied | Show URL to register; hybrid embedded key is a future story |
| Caching | In-memory, 30-minute TTL | Balance between freshness and performance |
| Platform | Windows-only | Simplifies credential storage; cross-platform is future |
| Accounts | Single account | Multi-account is a future story |

## 3. Project Structure

```
DbxProvider/
├── DbxProvider.sln
├── README.md
├── docs/
│   ├── DESIGN.md                          # This document
│   └── IMPLEMENTATION-PLAN.md             # Phased implementation plan
├── src/DbxProvider/
│   ├── DbxProvider.csproj                 # .NET 10 class library
│   ├── DbxProvider.psd1                   # Module manifest
│   ├── DbxProvider.format.ps1xml          # Custom formatting for items
│   ├── Provider/
│   │   ├── DbxDriveInfo.cs                # PSDriveInfo subclass (holds auth state, root path)
│   │   ├── DbxProvider.cs                 # Main provider (NavigationCmdletProvider)
│   │   ├── DbxContentReader.cs            # IContentReader (Get-Content → download)
│   │   ├── DbxContentWriter.cs            # IContentWriter (Set-Content → upload)
│   │   └── DbxItemProperty.cs             # IPropertyCmdletProvider (Get-ItemProperty)
│   ├── Api/
│   │   ├── DropboxApiClient.cs            # HttpClient wrapper for all Dropbox API calls
│   │   ├── DropboxEndpoints.cs            # Endpoint URL constants
│   │   └── Models/
│   │       ├── FileMetadata.cs            # Dropbox file metadata model
│   │       ├── FolderMetadata.cs          # Dropbox folder metadata model
│   │       ├── SearchResult.cs            # Search response model
│   │       ├── FileRevision.cs            # Revision/version model
│   │       └── ErrorResponse.cs           # API error model
│   ├── Auth/
│   │   ├── OAuthPkceFlow.cs               # PKCE OAuth implementation
│   │   ├── TokenManager.cs                # Token refresh, expiry management
│   │   └── CredentialStore.cs             # Windows Credential Manager read/write
│   └── Cache/
│       └── MetadataCache.cs               # In-memory TTL cache for metadata
├── tests/DbxProvider.Tests/
│   ├── DbxProvider.Tests.csproj
│   └── ...
└── publish/                                # Build output / packaging
```

## 4. Authentication Flow

### First-Time Use

1. `Import-Module DbxProvider` — provider checks Windows Credential Manager
2. No stored credential found → prompts user:
   - Shows: `"Register a Dropbox app at https://www.dropbox.com/developers/apps"`
   - Prompts for: App Key (client ID)
3. Initiates PKCE OAuth:
   a. Generates `code_verifier` (random 43-128 char string) + `code_challenge` (SHA256 + base64url)
   b. Opens browser to Dropbox authorization URL with `code_challenge`
   c. Starts localhost HTTP listener on an ephemeral port for the OAuth callback
   d. Receives authorization code via callback
   e. Exchanges auth code + `code_verifier` → `access_token` + `refresh_token`
4. Stores in Windows Credential Manager:
   - App Key
   - Refresh Token
   - Access Token + expiry timestamp
5. Drive is mounted and ready

### Subsequent Uses

1. Reads credentials from Credential Manager
2. If access token expired → uses refresh token to obtain new access token
3. Drive mounts immediately — no user interaction

### Re-Authentication

- If refresh token is revoked by Dropbox → `Write-Warning` prompts user to run `Connect-Dbx`

## 5. Provider Capabilities

### Base Class Hierarchy

```
NavigationCmdletProvider
├── IContentCmdletProvider       → Get-Content, Set-Content, Add-Content, Clear-Content
└── IPropertyCmdletProvider      → Get-ItemProperty
```

### Operation → API Mapping

| PowerShell Operation | Dropbox API Endpoint | Provider Method |
|---|---|---|
| `Get-ChildItem Dbx:\` | `files/list_folder` | `GetChildItems()` |
| `Get-Item Dbx:\path` | `files/get_metadata` | `GetItem()` |
| `Get-Content Dbx:\file.txt` | `files/download` | `GetContentReader()` |
| `Set-Content Dbx:\file.txt` | `files/upload` | `GetContentWriter()` |
| `Add-Content Dbx:\file.txt` | `files/upload` (append mode) | `GetContentWriter()` |
| `Clear-Content Dbx:\file.txt` | `files/upload` (empty) | `ClearContent()` |
| `New-Item -Type Directory` | `files/create_folder_v2` | `NewItem()` |
| `New-Item -Type File` | `files/upload` (empty) | `NewItem()` |
| `Remove-Item` | `files/delete_v2` | `RemoveItem()` |
| `Copy-Item` | `files/copy_v2` | `CopyItem()` |
| `Move-Item` / `Rename-Item` | `files/move_v2` | `MoveItem()` |
| `Test-Path` | `files/get_metadata` | `ItemExists()` |
| `Get-ItemProperty` | metadata fields | `GetProperty()` |
| Tab completion | `files/list_folder` (cached) | `GetChildNames()` |

### Cross-Provider Operations

`Copy-Item C:\local\file.txt Dbx:\uploaded.txt` works automatically — PowerShell reads via the source provider's `GetContentReader()` and writes via the destination provider's `GetContentWriter()`.

## 6. Additional Cmdlets

Exported alongside the provider module:

| Cmdlet | Dropbox API | Purpose |
|---|---|---|
| `Search-Dbx` | `files/search_v2` | Full-text search across Dropbox |
| `Get-DbxRevision` | `files/list_revisions` | List previous versions of a file |
| `Restore-DbxRevision` | `files/restore` | Restore a file to a previous revision |
| `Connect-Dbx` | OAuth PKCE | Manually trigger authentication / re-auth |
| `Disconnect-Dbx` | — | Clear stored credentials from Credential Manager |

## 7. Caching Strategy

```
MetadataCache
├── Implementation: ConcurrentDictionary<string, CacheEntry>
├── Key: Normalized Dropbox path (lowercase, forward-slash separated)
├── Value: Metadata object + DateTimeOffset expiry
├── Default TTL: 30 minutes
├── Bypass: -Force parameter on Get-ChildItem, Get-Item, etc.
├── Invalidation: Write operations (New/Remove/Copy/Move/Set-Content)
│                 automatically evict the affected path + its parent folder
└── Cleanup: Lazy eviction on access (no background timer needed)
```

## 8. Error Handling

| Scenario | Behavior |
|---|---|
| Access token expired | Auto-refresh via refresh token; retry the request transparently |
| Refresh token revoked | `Write-Warning` prompting user to run `Connect-Dbx` |
| Rate limited (HTTP 429) | Retry with `Retry-After` header value as backoff delay |
| Path not found (HTTP 409, `path/not_found`) | `ItemNotFoundException` → standard PowerShell error |
| Conflict (file exists on create) | `WriteError()` with `ResourceExists` category |
| Network failure | `WriteError()` with descriptive message and `ConnectionError` category |
| Other API errors | Parse Dropbox error JSON → `ErrorRecord` with appropriate `ErrorCategory` |

## 9. Drive Mounting Examples

```powershell
# Import module (auto-mounts Dbx:\ to root if credentials exist)
Import-Module DbxProvider

# Mount root
New-PSDrive -Name Dbx -PSProvider Dropbox -Root "/"

# Mount a subfolder as a drive root
New-PSDrive -Name Photos -PSProvider Dropbox -Root "/Photos"

# Navigate
cd Dbx:\
Get-ChildItem
Get-Content .\document.txt
Copy-Item .\file.txt .\backup\file.txt
Copy-Item C:\local\file.txt Dbx:\uploaded.txt
Get-ItemProperty .\file.txt
Search-Dbx -Query "quarterly report" -Path "/Work"
Get-DbxRevision .\file.txt
```

## 10. Deferred (Future Stories)

- **Large file upload sessions** — Files >150MB require Dropbox's `upload_session/start`, `append`, `finish` API. Currently limited to simple upload (<150MB).
- **Multiple Dropbox accounts** — Mount personal and work accounts as separate drives (`DbxPersonal:\`, `DbxWork:\`).
- **Hybrid/embedded App Key** — Ship a default App Key so users don't need to register their own app.
- **Cross-platform support** — Linux/macOS token storage (replace Windows Credential Manager with a portable solution).
