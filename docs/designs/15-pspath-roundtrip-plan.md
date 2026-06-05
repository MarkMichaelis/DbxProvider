# Fix: `$it.PSPath` round-trip for the Dbx provider (Issue #15)

## Design / Root cause

`Get-ChildItem Dbx:\` emits items whose `PSPath` is **provider-qualified** and
omits the drive: `DbxProvider\Dropbox::IntelliTect.Old(...)`. When that PSPath is
resolved back, PowerShell invokes the provider with a **null `PSDriveInfo`**
(empirically confirmed with a minimal toy NavigationCmdletProvider). The provider's
`GetService()` throws `InvalidOperationException` when `PSDriveInfo` is not a
`DropboxDriveInfo`; the exception is swallowed by the `catch` in `GetChildItems`,
so zero children are returned silently.

Well-behaved built-in providers (FileSystem, Registry) embed their root in the
provider-internal path and resolve from the path string, never relying on
`PSDriveInfo`. Empirically, `ProviderInfo.Drives` still lists the Dropbox drive
even when `PSDriveInfo` is null. So the fix is to resolve the service/cache from
`ProviderInfo.Drives` when `PSDriveInfo` is unavailable.

## Acceptance criteria (from issue #15)

- `Get-ChildItem Dbx:\X | %{ Get-ChildItem $_.PSPath }` returns X's children
- `Get-Item -LiteralPath $it.PSPath` returns the item; `$it | Get-ChildItem` works
- Behavior-first tests prove the round-trip and fail (0 children) when reverted
- No regression to `Get-ChildItem Dbx:\`, `-Recurse`, search routing, `Set-Location`

## Tasks

1. **Enable testability (Core)**  mark the read-path methods the provider calls
   `virtual`: `GetCurrentAccountAsync`, `ListFolderAsync`, `GetMetadataAsync`,
   `ItemExistsAsync`. (Allows an in-memory fake service for hosted-PowerShell tests.)

2. **Fix (Provider)**  in `DropboxProvider.GetService()` and `GetCache()`, fall
   back to `ProviderInfo.Drives` (the single Dropbox drive) when `PSDriveInfo` is
   not a `DropboxDriveInfo`. Keep the existing throw only when no Dropbox drive
   exists at all.

3. **Behavior-first test (xUnit, hosted PowerShell)**  new test project/harness
   that imports the built module, registers a drive backed by an in-memory fake
   service with tree `/A` containing `/A/b.txt`, and asserts:
   - `Get-ChildItem -LiteralPath $folder.PSPath` returns `b.txt`
   - `(Get-Item -LiteralPath $folder.PSPath).PSChildName -eq 'A'`
   - `$folder | Get-ChildItem` returns `b.txt`
   These fail (0 children) when task 2 is reverted.

4. **Unit tests (xUnit)**  `NormalizePath` maps root / relative / leading-slash /
   backslash / nested forms to the correct `/Foo/Bar`.

5. **Evidence**  capture before/after markdown artifact of the round-trip.

6. Build, `dotnet test`, `dotnet format`, PR with `Closes #15`, Copilot review,
   rebase-merge, cleanup.
