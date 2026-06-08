# Plan: Cache-backed conflict/name finding with cross-cutting auto-refresh

Issue: #47
Branch: feat/47-cache-backed-finding

## Goal

Make Find-DropboxConflict read the SQLite metadata cache BY DEFAULT (zero-API
enumeration), add a reusable cross-cutting cache auto-refresh, add a new
Find-DropboxItem cmdlet, retire the misleading API full-scan path, and migrate
legacy .state.json sidecars without data loss.

## Requirements -> tasks

### R1/R5 Core enumeration API (MetadataCache)
- [ ] T1. Add `EnumerateItems(string startPath = "")` to MetadataCache: stream every
      persisted entry's items under a subtree directly from SQLite (zero API). Flush
      dirty resident entries first, query folder keys under startKey (root => all),
      then per-key load + deserialize items_json under _diskLock (lock released per
      entry, never held across yield). 
- [ ] T2. Add `FindItems(Func<DropboxItem,bool> predicate, string startPath = "")`:
      EnumerateItems(startPath).Where(predicate), deduped by Path (OrdinalIgnoreCase).
- [ ] TEST (Dbx.Core.UnitTests/MetadataCacheEnumerateTests.cs): after BuildAsync over
      SampleTree, snapshot fake API call counters; assert EnumerateItems("") returns all
      items with ZERO extra listing calls; subtree scoping returns only that subtree;
      FindItems(zero-byte conflict predicate) returns exactly the conflict file.

### R3 Retire API full-scan path
- [ ] T3. New file src/IntelliTect.Dropbox.Core/ConflictMatch.cs: move `ConflictMatch`
      (Path, Bytes) here; add `LegacyConflictScanState` (AccountId, StartPath, Pattern,
      IncludeNonZero, Cursor, Dictionary<string,ConflictMatch> Matches; static FromJson
      returns null on invalid; PropertyNameCaseInsensitive).
- [ ] T4. DELETE src/IntelliTect.Dropbox.Core/ConflictScanner.cs and
      test/Dbx.Core.UnitTests/ConflictScannerTests.cs.

### R2 Cross-cutting auto-refresh (DropboxCmdletBase)
- [ ] T5. Add `protected MetadataCache GetRefreshedCache()` to DropboxCmdletBase:
      GetService(); cache = CacheCmdletHelpers.GetCache(this, DriveName); if cache
      disabled return it; if GetSyncState()==null => Run(EnsureSyncCursorAsync)+verbose,
      skip drain; else transient WriteProgress, sync = Run(SyncAsync); if ResetRequired =>
      complete progress + WriteWarning "run Build-DropboxCacheAll.ps1 -Rebuild"; else
      completed progress + WriteVerbose "Refreshed cache: N added, M removed.".
- [ ] T6. Add `protected static string StripDrivePrefix(string path)` to base (moved from
      ConflictCommands).

### R5 Find-DropboxItem cmdlet
- [ ] T7. New cmdlet FindDropboxItemCommand [Find, "DropboxItem"], OutputType DropboxItem:
      -Name (wildcard, pos 0, default *), -Path (default root), -ZeroByteOnly switch.
      `internal static Func<DropboxItem,bool> BuildNamePredicate(string namePattern, bool
      zeroByteOnly)` using WildcardMatcher. cache=GetRefreshedCache(); warn if
      PersistedCount()==0; WriteObject each FindItems(pred,start).
- [ ] T8. Add 'Find-DropboxItem' to DbxProvider.psd1 CmdletsToExport.

### R1/R4 Rewrite Find-DropboxConflict
- [ ] T9. Rewrite FindDropboxConflictCommand: keep OutputType ConflictMatch; params
      -Path, -Pattern (default *'s conflicted copy*), -IncludeNonZero, -StatePath (legacy
      migration only), -DriveName. REMOVE -Full. Delegate to BuildNamePredicate(Pattern,
      zeroByteOnly: !IncludeNonZero); matches = cache.FindItems(i => !i.IsFolder &&
      pred(i), start); project to ConflictMatch{Path,Bytes=Length}. 
- [ ] T10. Migration: MigrateLegacyStateIfPresent(statePath) - if file exists and parses
      as LegacyConflictScanState, File.Move to <path>.bak (unique), WriteWarning with the
      saved-match count + Build-DropboxCacheAll.ps1 hint. Check explicit -StatePath and
      the legacy default temp path (%TEMP%/DbxProvider/conflict-scan-<hash>.json).

### Tests (host)
- [ ] T11. Extend FakeDropboxServiceClient with sync support (GetLatestCursorAsync ->
      "sync::N" + counter; ListFolderContinueRawAsync handles "sync::" cursors via a new
      Queue<ListFolderDelta> SyncDeltas; keep existing _scriptedDeltas).
- [ ] T12. Rewrite ConflictCmdletHostTests: InitializeCache + Build before
      Find-DropboxConflict; assert cached conflict returned.
- [ ] T13. New FindDropboxItemHostTests: (a) scripted sync delta adds a conflict ->
      appears in results + verbose "1 added"; (b) ResetRequired -> warning mentioning
      Build-DropboxCacheAll.ps1 -Rebuild; (c) no cursor -> baseline captured
      (GetLatestCursorCalls==1), no drain.
- [ ] T14. New migration host test: legacy .state.json archived to .bak, no error,
      cached conflict still returned.

### Docs / script
- [ ] T15. Create docs/help/en-US/Find-DropboxItem.md (every declared param documented).
- [ ] T16. Update docs/help/en-US/Find-DropboxConflict.md (remove -Full; fix -StatePath &
      DESCRIPTION to cache-backed reality; update SYNTAX).
- [ ] T17. Rewrite Find-DropboxConflicts.ps1 docstrings honestly (cache-backed,
      auto-refreshed, zero-API; mention Build-DropboxCacheAll.ps1); drop -Full; keep
      -StatePath pass-through for legacy migration; keep $m.Bytes/$m.Path.
- [ ] T18. Optional Pester Find-DropboxItem.Tests.ps1 mirroring Search-Dropbox.Tests.ps1
      (credential-gated).

## Verification (env: $env:DbxSkipHelpBuild='true'; $env:DOTNET_ROLL_FORWARD='LatestMajor')
- dotnet test test\Dbx.Core.UnitTests\Dbx.Core.UnitTests.csproj
- dotnet test test\DbxProvider.ProviderHostTests\DbxProvider.ProviderHostTests.csproj
- dotnet format (new/changed files only; DO NOT touch CredentialCommands.cs /
  DbxCredentialStore.cs pre-existing format errors)
- Release build + build\Build-Help.ps1 => "Help completeness gate passed."

## Notes / constraints
- Behavior-first TDD: each test must fail for a behavioral reason when production reverted.
- Repo: REBASE MERGES ONLY. PR/issue bodies via --body-file, UTF-8 no BOM, ASCII only.
- Commit only from this worktree. Pass --repo MarkMichaelis/DbxProvider to gh.
- Do NOT touch Pull-SDLC.ai.ps1 (untracked, out of scope).