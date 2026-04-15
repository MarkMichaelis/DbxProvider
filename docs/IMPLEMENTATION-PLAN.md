# DbxProvider — Implementation Plan

**Status:** Not started  
**Date:** 2026-04-15  
**See also:** [DESIGN.md](DESIGN.md)

---

## Phase 1: Project Scaffolding

- [ ] Create `DbxProvider.sln` solution
- [ ] Create `src/DbxProvider/DbxProvider.csproj` (.NET 10 class library targeting PowerShell 7+)
- [ ] Add `System.Management.Automation` package reference
- [ ] Create `tests/DbxProvider.Tests/DbxProvider.Tests.csproj` (xUnit)
- [ ] Create folder structure: `Provider/`, `Api/`, `Api/Models/`, `Auth/`, `Cache/`
- [ ] Create module manifest `DbxProvider.psd1`
- [ ] Verify solution builds cleanly

## Phase 2: Authentication

- [ ] Implement `CredentialStore.cs` — Read/write App Key, refresh token, and access token to Windows Credential Manager
- [ ] Implement `OAuthPkceFlow.cs` — PKCE code verifier/challenge generation, browser launch, localhost HTTP listener for callback, token exchange
- [ ] Implement `TokenManager.cs` — Check token expiry, auto-refresh using refresh token, surface re-auth prompt when refresh token is revoked
- [ ] Write tests for PKCE challenge generation and token refresh logic
- [ ] Manual integration test: run OAuth flow against Dropbox, verify token storage

## Phase 3: Dropbox API Client

- [ ] Implement `DropboxEndpoints.cs` — Constants for all endpoint URLs
- [ ] Implement API models: `FileMetadata.cs`, `FolderMetadata.cs`, `ErrorResponse.cs`
- [ ] Implement `DropboxApiClient.cs` with methods:
  - `ListFolderAsync(path)` → `files/list_folder` + `list_folder/continue` for pagination
  - `GetMetadataAsync(path)` → `files/get_metadata`
  - `DownloadAsync(path)` → `files/download` (returns stream)
  - `UploadAsync(path, stream, mode)` → `files/upload`
  - `CreateFolderAsync(path)` → `files/create_folder_v2`
  - `DeleteAsync(path)` → `files/delete_v2`
  - `CopyAsync(from, to)` → `files/copy_v2`
  - `MoveAsync(from, to)` → `files/move_v2`
  - `SearchAsync(query, path)` → `files/search_v2`
  - `ListRevisionsAsync(path)` → `files/list_revisions`
  - `RestoreAsync(path, rev)` → `files/restore`
- [ ] Add error handling: parse Dropbox error responses, handle 429 rate limiting with retry
- [ ] Add automatic token refresh on 401 responses
- [ ] Write unit tests with mocked HttpClient

## Phase 4: Metadata Cache

- [ ] Implement `MetadataCache.cs`:
  - `ConcurrentDictionary<string, CacheEntry>` with normalized path keys
  - 30-minute default TTL
  - `TryGet(path)` — returns cached metadata or null if expired
  - `Set(path, metadata)` — stores with expiry timestamp
  - `Evict(path)` — removes single entry
  - `EvictPrefix(path)` — removes entry and its parent (for write invalidation)
  - `Clear()` — flush entire cache
- [ ] Write unit tests for TTL expiry, eviction, and cache bypass

## Phase 5: Core Provider (Read Operations)

- [ ] Implement `DbxDriveInfo.cs` — Subclass of `PSDriveInfo` holding `DropboxApiClient`, `MetadataCache`, root path
- [ ] Implement `DbxProvider.cs` (partial — read operations):
  - `[CmdletProvider("Dropbox", ProviderCapabilities.None)]` attribute
  - `InitializeDefaultDrives()` — auto-mount `Dbx:\` if credentials exist
  - `NewDrive()` / `RemoveDrive()` — create/tear down `DbxDriveInfo`
  - Path manipulation: `IsValidPath()`, `NormalizePath()`, `GetDropboxPath()`
  - `ItemExists()` → `GetMetadataAsync` (cached)
  - `IsItemContainer()` → check if metadata is folder
  - `GetItem()` → `GetMetadataAsync` → write `PSObject` to pipeline
  - `GetChildItems()` → `ListFolderAsync` (cached)
  - `GetChildNames()` → `ListFolderAsync` (cached, names only for tab-complete)
  - `HasChildItems()` → `ListFolderAsync` (cached)
- [ ] Create `DbxProvider.format.ps1xml` — table format for file/folder items
- [ ] Manual test: `Import-Module`, `cd Dbx:\`, `Get-ChildItem`, `Get-Item`, `Test-Path`

## Phase 6: Content Operations (Get-Content / Set-Content)

- [ ] Implement `DbxContentReader.cs` — `IContentReader`:
  - `Read(readCount)` → stream from `DownloadAsync`, return lines or byte blocks
  - `Close()` / `Dispose()` — clean up stream
- [ ] Implement `DbxContentWriter.cs` — `IContentWriter`:
  - Buffer writes, upload on `Close()`
  - Support overwrite mode (Set-Content) and append mode (Add-Content)
- [ ] Wire into `DbxProvider.cs`: `GetContentReader()`, `GetContentWriter()`, `ClearContent()`
  - `ClearContent()` → upload empty file
- [ ] Invalidate cache on write operations
- [ ] Manual test: `Get-Content Dbx:\file.txt`, `Set-Content`, `Add-Content`, cross-provider copy

## Phase 7: Write Operations (New / Remove / Copy / Move)

- [ ] Implement in `DbxProvider.cs`:
  - `NewItem()` → `CreateFolderAsync` or `UploadAsync` (empty file) based on `-ItemType`
  - `RemoveItem()` → `DeleteAsync` (support `-Recurse` for folders)
  - `CopyItem()` → `CopyAsync` (Dropbox-to-Dropbox only; cross-provider handled by PowerShell)
  - `MoveItem()` → `MoveAsync` (also handles `Rename-Item`)
- [ ] Cache invalidation: evict source path, destination path, and parent folders
- [ ] Manual test: `New-Item`, `Remove-Item`, `Copy-Item`, `Move-Item`, `Rename-Item`

## Phase 8: Item Properties

- [ ] Implement `IPropertyCmdletProvider` on `DbxProvider.cs`:
  - `GetProperty()` → expose: `rev`, `content_hash`, `size`, `server_modified`, `client_modified`, `path_display`, `path_lower`, `id`
  - `GetPropertyDynamicParameters()` — allow selecting specific properties
- [ ] Manual test: `Get-ItemProperty Dbx:\file.txt`

## Phase 9: Additional Cmdlets

- [ ] Implement `Search-Dbx` cmdlet:
  - Parameters: `-Query` (string), `-Path` (optional Dropbox path to scope search)
  - Calls `SearchAsync`, returns formatted results
- [ ] Implement `Get-DbxRevision` cmdlet:
  - Parameters: `-Path` (Dropbox file path), `-Limit` (optional)
  - Model: `FileRevision.cs`
  - Calls `ListRevisionsAsync`, returns revision list
- [ ] Implement `Restore-DbxRevision` cmdlet:
  - Parameters: `-Path`, `-Rev`
  - Calls `RestoreAsync`, invalidates cache
  - Add `-Confirm` / `ShouldProcess` support
- [ ] Implement `Connect-Dbx` cmdlet:
  - Re-run OAuth PKCE flow, update Credential Manager
- [ ] Implement `Disconnect-Dbx` cmdlet:
  - Clear all stored credentials from Credential Manager
  - Add `-Confirm` / `ShouldProcess` support
- [ ] Write tests for cmdlet parameter validation

## Phase 10: Error Handling & Polish

- [ ] Verify all error paths produce proper `ErrorRecord` objects with correct `ErrorCategory`
- [ ] Implement retry logic for HTTP 429 (rate limiting) with `Retry-After` backoff
- [ ] Implement automatic token refresh on HTTP 401 with transparent retry
- [ ] Add `-Force` dynamic parameter support on `Get-Item`, `Get-ChildItem` to bypass cache
- [ ] Handle `-Recurse` properly on `Remove-Item` and `Get-ChildItem`
- [ ] Add `about_Dropbox.help.txt` help file
- [ ] Update `README.md` with installation and usage instructions
- [ ] Final build and integration test pass

## Phase 11: Packaging & Distribution

- [ ] Configure `publish/` output with module files
- [ ] Create build script (`publish.ps1` or MSBuild targets)
- [ ] Test: `Import-Module ./publish/DbxProvider` end-to-end
- [ ] Document publish steps in README
