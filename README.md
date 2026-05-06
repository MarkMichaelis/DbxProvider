![CI](https://github.com/<owner>/<repo>/actions/workflows/ci.yml/badge.svg)
<!-- Replace <owner>/<repo> above with the actual GitHub owner and repository name. -->

# DbxProvider - PowerShell Dropbox Provider

A comprehensive PowerShell module that exposes the full Dropbox API as a PowerShell provider and cmdlet set. Navigate your Dropbox like a file system and manage sharing, revisions, tags, locks, and more.

## Requirements

- PowerShell 7.4+
- .NET 8.0 SDK (for building)
- A Dropbox account and access token or app key

## Building

```powershell
dotnet build src\DbxProvider\DbxProvider.csproj -c Release
```

The build also compiles the cmdlet help (PlatyPS markdown under
`docs\help\en-US\` &rarr; `en-US\DbxProvider.dll-Help.xml` next to the
module DLL) and **fails the build** if any exported cmdlet is missing
help, examples, or parameter descriptions. To bypass the help build
during inner-loop iteration, set `DbxSkipHelpBuild=true`:

```powershell
$env:DbxSkipHelpBuild = 'true'
dotnet build src\DbxProvider\DbxProvider.csproj -c Release
```

## Authoring help for a new cmdlet

Help is authored in PlatyPS markdown under `docs\help\en-US\`. To add
a new cmdlet:

1. Implement the cmdlet in C# and add it to `CmdletsToExport` in
   `src\DbxProvider\DbxProvider.psd1`.
2. Build once to produce the assembly:
   ```powershell
   $env:DbxSkipHelpBuild = 'true'
   dotnet build src\DbxProvider\DbxProvider.csproj -c Release
   Remove-Item env:DbxSkipHelpBuild
   ```
3. Refresh the markdown so the new cmdlet gets a stub and existing
   stubs pick up any parameter changes:
   ```powershell
   pwsh .\build\Build-Help.ps1 -Update
   ```
   `-Update` calls PlatyPS `Update-MarkdownHelp`, which refreshes the
   parameter blocks from the assembly **without clobbering authored
   prose**. Do not hand-edit the YAML blocks under each parameter; let
   `Update-MarkdownHelp` regenerate them.
4. Edit `docs\help\en-US\<NewCmdlet>.md`: replace the
   `{{ Fill ... }}` placeholders with a real Synopsis, Description,
   at least one Example with a fenced PowerShell code block, and a
   description for every parameter.
5. Re-run `dotnet build` (or `pwsh .\build\Build-Help.ps1`) to compile
   the markdown to MAML and run the completeness gate.


## Installation

```powershell
# After building, import the module:
Import-Module .\src\DbxProvider\bin\Release\net8.0\DbxProvider.dll

# Or copy the output to a PowerShell module directory:
$modPath = "$env:USERPROFILE\Documents\PowerShell\Modules\DbxProvider\1.0.0"
Copy-Item .\src\DbxProvider\bin\Release\net8.0\* $modPath -Recurse
Import-Module DbxProvider
```

## Quick Start

```powershell
# Connect with an access token
Connect-Dropbox -AccessToken "your-access-token"

# Or use OAuth2 flow (opens browser)
Connect-Dropbox -AppKey "your-app-key" -DriveName Dbx

# Navigate Dropbox like a file system
cd Dbx:\
dir                            # Get-ChildItem  - list folder
dir -Recurse                   # Recursive listing
cd Documents                   # Set-Location   - navigate
cat readme.txt                 # Get-Content    - read file
"Hello" | Set-Content test.txt # Set-Content    - write file

# File operations
mkdir "New Folder"             # New-Item -ItemType Directory
copy file.txt backup.txt       # Copy-Item
move old.txt new.txt           # Move-Item / Rename-Item
del unwanted.txt               # Remove-Item
del junk.txt -Force            # Permanently delete

# Check existence
Test-Path Documents\report.docx
```

## Provider Operations (Standard Cmdlets)

| PowerShell Cmdlet     | Dropbox API               | Description                  |
|-----------------------|---------------------------|------------------------------|
| `Get-ChildItem`       | list_folder               | List folder contents         |
| `Get-ChildItem -Rec`  | list_folder (recursive)   | List all items recursively   |
| `Get-Item`            | get_metadata              | Get file/folder metadata     |
| `Test-Path`           | get_metadata              | Check if item exists         |
| `New-Item -Type Dir`  | create_folder_v2          | Create a folder              |
| `New-Item`            | upload                    | Create a file with content   |
| `Remove-Item`         | delete_v2                 | Delete file/folder           |
| `Remove-Item -Force`  | permanently_delete        | Permanently delete           |
| `Copy-Item`           | copy_v2                   | Copy file/folder             |
| `Move-Item`           | move_v2                   | Move file/folder             |
| `Rename-Item`         | move_v2                   | Rename file/folder           |
| `Get-Content`         | download                  | Download/read file content   |
| `Set-Content`         | upload                    | Upload/write file content    |
| `Clear-Content`       | upload (empty)            | Clear file content           |
| `Get-ItemProperty`    | get_metadata              | Get detailed metadata        |

## Custom Cmdlets

### Authentication
```powershell
# Token-based auth
Connect-Dropbox -AccessToken "sl.xxxxx"

# OAuth2 with PKCE (interactive, opens browser)
Connect-Dropbox -AppKey "your-app-key"

# Disconnect
Disconnect-Dropbox
```

### Search
```powershell
# Token search (default): "quarterly" AND "report" as prefix tokens against
# both filenames and file contents.
Search-Dropbox "quarterly report"
Search-Dropbox "budget" -Path "/Finance" -MaxResults 50

# Restrict matching to filenames (skip content indexing — faster)
Search-Dropbox "budget" -FilenameOnly

# Server-side extension filter — the right way to do "*.docx"
Search-Dropbox "report" -FileExtensions pdf,docx,xlsx

# Server-side category filter
Search-Dropbox "kickoff" -FileCategory Document,Paper,Spreadsheet

# Search deleted files
Search-Dropbox "old-contract" -FileStatus Deleted

# Order by last modified instead of relevance
Search-Dropbox "invoice" -OrderBy LastModifiedTime

# True PowerShell-wildcard semantics (post-filtered with WildcardPattern)
Search-Dropbox "*.docx"   -Wildcard
Search-Dropbox "Q4*.xlsx" -Wildcard -Path /Finance/2025
```

> **Note on Dropbox search semantics.** `files/search_v2` is *prefix-token-based*,
> not glob. A query like `"*.docx"` is split on punctuation/wildcards and
> tokens are prefix-matched against filename tokens (and, by default, file
> contents). `*` and `?` are treated as literals and effectively ignored.
> Use `-FileExtensions` for server-side extension filtering, or `-Wildcard`
> to apply true PowerShell glob semantics on top of the search results.

### Provider performance — when wildcards use search

The provider's `Get-ChildItem`/`Test-Path` automatically route to the indexed
`search_v2` API when the scope is already a subtree, so you don't have to
walk every folder yourself:

| Invocation                                  | Route                                   |
|---------------------------------------------|-----------------------------------------|
| `dir`                                       | `list_folder`                            |
| `dir *.dbx` (single folder, leaf wildcard)  | `list_folder` + client-side filter      |
| `dir -Recurse`                              | `list_folder` (recursive)                |
| `dir -Recurse *.dbx` / `-Filter *.dbx`      | **`search_v2`** filename-only, post-filtered |
| `dir Dbx:\**\*.dbx` (deep path wildcard)    | **`search_v2`** scoped to non-wildcard ancestor |
| `Test-Path Dbx:\**\foo.docx`                | single **`search_v2`** call             |

Use `-NoSearch` to force the list-based path (e.g. right after uploads while
the search index is still propagating):

```powershell
Get-ChildItem Dbx:\Finance -Recurse -Filter *.xlsx -NoSearch
```

### File Transfer (Large File Support)
```powershell
# Upload local file (auto-handles files >150MB via upload sessions)
Invoke-DropboxUpload -Source C:\local\bigfile.zip -DropboxPath /Backups/bigfile.zip

# Download to local
Invoke-DropboxDownload -Path /Documents/report.pdf -Destination C:\local\report.pdf
```

### Revisions
```powershell
# List file revisions
Get-DropboxRevision /Documents/report.docx

# Restore to previous revision
Restore-DropboxRevision -Path /Documents/report.docx -Rev "015f11a4362"
```

### Shared Links
```powershell
# Create shared link
New-DropboxSharedLink /Documents/report.pdf -Visibility public

# List shared links
Get-DropboxSharedLink
Get-DropboxSharedLink /Documents/report.pdf

# Get metadata for a shared link URL
Get-DropboxSharedLink -Url "https://www.dropbox.com/s/xxxxx/file.pdf"

# Revoke a shared link
Remove-DropboxSharedLink -Url "https://www.dropbox.com/s/xxxxx/file.pdf"
```

### Folder Sharing
```powershell
# Share a folder
Add-DropboxSharedFolder /Projects/TeamProject

# List shared folders
Get-DropboxSharedFolder

# Unshare
Remove-DropboxSharedFolder -SharedFolderId "84528192421"
```

### Members
```powershell
# Add member to shared folder
Add-DropboxMember -SharedFolderId "84528192421" -Email "colleague@example.com" -AccessLevel editor

# Add member to file
Add-DropboxMember -FilePath /Documents/report.pdf -Email "reviewer@example.com" -AccessLevel viewer

# List members
Get-DropboxMember -SharedFolderId "84528192421"
Get-DropboxMember -FilePath /Documents/report.pdf

# Remove member
Remove-DropboxMember -SharedFolderId "84528192421" -Email "former@example.com"
```

### Tags
```powershell
# Add tag
Add-DropboxTag /Documents/report.pdf -Tag "quarterly"

# Get tags
Get-DropboxTag /Documents/report.pdf

# Remove tag
Remove-DropboxTag /Documents/report.pdf -Tag "quarterly"
```

### File Locks
```powershell
# Lock files
Lock-DropboxFile /Documents/report.docx

# Check lock status
Get-DropboxFileLock /Documents/report.docx

# Unlock
Unlock-DropboxFile /Documents/report.docx
```

### Temporary Links & URL Saving
```powershell
# Get a temporary direct download link (4 hours)
Get-DropboxTemporaryLink /Documents/report.pdf

# Save a URL to Dropbox (Dropbox downloads it)
Save-DropboxUrl -DropboxPath /Downloads/file.zip -Url "https://example.com/file.zip"
```

### Previews & Thumbnails
```powershell
# Get file preview (PDF)
Get-DropboxPreview /Documents/report.docx -OutFile C:\temp\preview.pdf

# Get thumbnail
Get-DropboxThumbnail /Photos/vacation.jpg -Size w256h256 -Format png -OutFile C:\temp\thumb.png
```

### Paper Documents
```powershell
# Create a Paper doc
New-DropboxPaper /Documents/notes.paper -Content "# My Notes`nSome content" -ImportFormat markdown

# Update a Paper doc
Set-DropboxPaper /Documents/notes.paper -Content "Updated content" -UpdatePolicy append
```

### Export
```powershell
# Export a cloud doc (Google Docs, etc.) to a downloadable format
Export-DropboxFile /Documents/gdoc.gdoc -OutFile C:\temp\exported.docx
```

### Batch Operations
```powershell
# Batch copy
Copy-DropboxItemBatch -FromPath @("/a/1.txt","/a/2.txt") -ToPath @("/b/1.txt","/b/2.txt")

# Batch move
Move-DropboxItemBatch -FromPath @("/a/1.txt","/a/2.txt") -ToPath @("/b/1.txt","/b/2.txt")

# Batch delete
Remove-DropboxItemBatch -Path @("/old/file1.txt", "/old/file2.txt")
```

### Account Information
```powershell
# Get current account info
Get-DropboxAccount

# Get another user account by ID
Get-DropboxAccount -AccountId "dbid:xxxxx"

# Check space usage
Get-DropboxSpaceUsage
```

## API Coverage

This module covers the complete Dropbox API v2:

| API Namespace | Endpoints Covered |
|---------------|-------------------|
| **Files**     | list_folder, get_metadata, download, upload, upload_session/*, copy_v2, move_v2, delete_v2, permanently_delete, create_folder_v2, search_v2, list_revisions, restore, get_preview, get_thumbnail_v2, get_temporary_link, save_url, copy_batch_v2, move_batch_v2, delete_batch, export, paper/create, paper/update, tags/add, tags/remove, tags/get, lock_file_batch, unlock_file_batch, get_file_lock_batch |
| **Sharing**   | create_shared_link_with_settings, list_shared_links, revoke_shared_link, get_shared_link_metadata, share_folder, unshare_folder, list_folders, get_folder_metadata, add_folder_member, remove_folder_member, list_folder_members, add_file_member, remove_file_member_v2, list_file_members |
| **Users**     | get_current_account, get_account, get_space_usage |

## Pipeline Support

Many cmdlets support pipeline input for efficient batch processing:

```powershell
# Search and download all matches
Search-Dropbox "report" | ForEach-Object {
    Invoke-DropboxDownload -Path $_.Item.Path -Destination "C:\reports\$($_.Item.Name)"
}

# Get revisions for multiple files
Get-ChildItem Dbx:\Documents\*.docx | Get-DropboxRevision

# Tag all files in a folder
Get-ChildItem Dbx:\ProjectX | ForEach-Object { Add-DropboxTag -Path $_.Path -Tag "projectx" }
```

## Rate limiting and cancellation

Dropbox throttles aggressive callers in several ways. DbxProvider
classifies each transient response and retries it transparently while
honoring `Ctrl+C`:

| Class                 | Detection                                                                              | Wait policy                          |
|-----------------------|----------------------------------------------------------------------------------------|--------------------------------------|
| **HTTP 429**          | `Dropbox.Api.RateLimitException` (gateway rate limit)                                  | server `Retry-After`, 5 s fallback   |
| **Soft throttle**     | `ApiException<T>` with body tag `too_many_write_operations` / `too_many_files` / `too_many_requests` / `*_rate_limit` | exponential 1 → 30 s (capped)        |
| **HTTP 5xx / 408**    | `Dropbox.Api.HttpException` with status 408 / 500 / 502 / 503 / 504                    | exponential 1 → 30 s (capped)        |
| Anything else         | propagates immediately — including `HttpRequestException` and other socket-level errors (those are connectivity loss, not throttling, and are out of scope here) | n/a |

Each retry emits a `Write-Warning` such as
`Dropbox returned a transient error (HTTP 429 (gateway rate limit)).
Waiting 5s before retry. Press Ctrl+C to cancel.` Verbose details
(attempt number, cumulative wait, classified reason) are emitted via
`Write-Verbose`:

```powershell
Get-ChildItem Dbx:\ -Verbose
```

`Ctrl+C` is honored at any time, including while waiting out a retry.
Cancellation surfaces as a normal pipeline-stopped error.

### CI guardrail — no operation can retry forever

For interactive use the retry loop is unbounded (you have `Ctrl+C`).
CI has no human in the loop, so retries are bounded by an
**elapsed-wall-clock budget per call**. If the next wait would push
cumulative retry time past the budget, the original Dropbox exception
is re-thrown (wrapped as `RetryBudgetExhaustedException`) so the test
fails with a clear cause rather than timing out.

The budget is resolved in this precedence order:

1. `DBX_RETRY_MAX_ELAPSED_SECONDS` env var (any integer ≥ 0; `0`
   disables retry entirely, useful for tests).
2. **Auto-detect CI**: if `CI=true` or `GITHUB_ACTIONS=true`, default
   to **120 s**.
3. Otherwise (interactive): unbounded.

A second safety net (per-class attempt cap of 1000) only activates
when the elapsed budget is unset, defending against pathological
loops where every wait collapses to ≈ 0 s.

### Demoing locally

`DBX_SIMULATE_RATELIMIT`, `DBX_SIMULATE_SOFT_RATELIMIT`, and
`DBX_SIMULATE_SERVER_ERROR` inject synthetic transient failures so
you can exercise each arm of the retry helper without actually
contacting Dropbox. Format is `count[:detail]`:

```powershell
$env:DBX_SIMULATE_RATELIMIT      = '3:5'                          # 3 fake HTTP-429s, 5s Retry-After each
$env:DBX_SIMULATE_SOFT_RATELIMIT = '3:too_many_write_operations'  # 3 soft throttles
$env:DBX_SIMULATE_SERVER_ERROR   = '2:503'                        # 2 fake HTTP-503s
Get-ChildItem Dbx:\ -Verbose       # press Ctrl+C any time during waits
Remove-Item Env:\DBX_SIMULATE_RATELIMIT, Env:\DBX_SIMULATE_SOFT_RATELIMIT, Env:\DBX_SIMULATE_SERVER_ERROR
```

Each simulator re-reads its environment variable every time the value
changes, so you can re-arm mid-session without restarting PowerShell
(append `#anything` to force a re-arm without changing the count).

`build\Demo-RateLimitRetry.ps1` wraps the whole flow (build, connect,
arm, list, cleanup) and supports
`-Mode Quick|Long|SoftThrottle|ServerError|Real|Hammer` for a guided
walk-through.

### Future work: connectivity-loss probe

Connection timeouts, DNS failures, and TLS resets are a categorically
different problem from throttling — the server isn't asking us to
slow down, it's unreachable. They are intentionally **not** retried
by the helper above; a follow-up PR will add an AIMD/hill-climbing
connectivity probe with its own UX ("Lost connectivity to Dropbox;
probing every Ns…") and cancellation semantics.

## Testing

DbxProvider ships with two test suites that run against the **real** Dropbox
API: a C# xUnit functional suite (`test\DbxProvider.FunctionalTests`) and a
Pester suite (`test\DbxProvider.Pester`). All tests isolate writes to
`/DbxProviderTests/` on the live account, and that folder is purged at the
**start** of each run (artifacts from the previous run remain visible until
then for post-mortem inspection).

### Running locally

The simplest path is to authenticate once with the normal `Connect-Dropbox`
cmdlet — it already runs OAuth + PKCE and persists `AppKey`, `AppSecret`,
and `RefreshToken` to the encrypted credential store. The test fixtures pick
those up automatically:

```powershell
# 1. One-time: build the module and authenticate. This populates
#    %LOCALAPPDATA%\DbxProvider\credentials.json (DPAPI-encrypted on Windows).
dotnet build src\DbxProvider\DbxProvider.csproj -c Release
Import-Module .\src\DbxProvider\bin\Release\net8.0\DbxProvider.dll
Connect-Dropbox -AppKey <key> -AppSecret <secret>

# 2. Build + run both test suites (same entry point CI uses).
pwsh ./build/Build-And-Test.ps1
```

If you prefer to avoid the encrypted credential store — for example to run
multiple distinct test accounts on one machine, or to mirror exactly how
CI sees its secrets — use `dotnet user-secrets` instead:

```powershell
pwsh ./build/Set-LocalSecrets.ps1
```

`Build-And-Test.ps1` accepts `-Configuration`, `-SkipFunctional`,
`-SkipPester`, and `-IncludeLargeFileTests` (the latter enables the
opt-in >150 MB chunked-upload tests by setting `DBX_RUN_LARGE_FILE_TESTS=1`).

Credential resolution order in both test suites:
**environment variables → `dotnet user-secrets` → `CredentialStore` (the file
written by `Connect-Dropbox`)**.

### Required GitHub Secrets

The CI workflow (`.github/workflows/ci.yml`) reads the following repository
secrets and exposes them as environment variables to the test process:

| Secret                  | Required | Purpose                                              |
|-------------------------|----------|------------------------------------------------------|
| `DBX_APP_KEY`           | yes      | Dropbox app key                                      |
| `DBX_APP_SECRET`        | yes      | Dropbox app secret                                   |
| `DBX_REFRESH_TOKEN`     | yes      | Long-lived refresh token used by the test fixture    |
| `DBX_TEST_MEMBER_EMAIL` | optional | Gates the multi-user sharing/member tests            |

Locally these same names are stored via `dotnet user-secrets` against
`test\DbxProvider.FunctionalTests\DbxProvider.FunctionalTests.csproj`
(`Set-LocalSecrets.ps1` handles the plumbing).

## License

MIT