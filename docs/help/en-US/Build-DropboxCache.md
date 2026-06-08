---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Build-DropboxCache

## SYNOPSIS

Pre-populates the metadata cache for a subtree by walking a recursive
`/files/list_folder` page by page (resumable), and can optionally cache each
file's revision history.

## SYNTAX

```
Build-DropboxCache [[-Path] <String>] [-IncludeRevisions] [-Refresh] [-Rebuild] [-DriveName <String>]
 [-ProgressAction <ActionPreference>] [-WhatIf] [-Confirm] [<CommonParameters>]
```

## DESCRIPTION

Walks the Dropbox tree under `-Path` with a recursive `list_folder`, reading the
listing one page at a time. After every page the per-folder entries and an
in-progress cursor are flushed to the cache database, so an interrupted build
(Ctrl+C, network drop, or process exit) resumes from the last completed page on
the next invocation instead of starting over. When Dropbox reports that a saved
cursor is no longer valid, the build restarts cleanly from the first page.

The recursive listing also requests media info and explicit-shared-member
flags, which Dropbox returns at no extra request cost, so the cached entries are
enriched with that metadata as a side effect of the build.

Because a recursive listing returns only a single subtree cursor (not a cursor
per folder), the entries created by this command start without a per-folder
cursor. Each built entry acquires its real per-folder cursor automatically on
its first validated read (the first `Get-ChildItem` or `Update-DropboxCache`
for that folder), after which it behaves like any other cursor-validated entry.
Dropbox always remains the master: a built entry is never served without first
reconciling against the server.

With `-IncludeRevisions`, the command runs a second pass that fetches and caches
the revision history of every file in the subtree. Files whose revisions were
fetched recently are skipped, so this pass is also resumable and cheap to
repeat. Progress is reported via `Write-Progress`.

On a normal build the command also captures an account-wide Dropbox delta cursor
(if one is not already saved) before walking the tree. This cursor anchors the
incremental refresh. Pass `-Refresh` to instead drain every change since that
cursor into the cache -- a cheap update that avoids re-walking the whole account.
Pass `-Rebuild` to wipe the cache (entries and build progress) and rebuild from a
freshly captured cursor. `-Refresh` and `-Rebuild` cannot be combined.

If the cache is disabled (`Set-DropboxCacheOption -Disable`), the command warns
and does nothing.

The command supports `-WhatIf`/`-Confirm` and emits a summary object reporting
the number of folders cached, items found, and (when `-IncludeRevisions` is
used) files and revisions cached.

## EXAMPLES

### Example 1
```powershell
PS> Build-DropboxCache
```

Pre-populates the cache for the entire drive starting at the root `/`.

### Example 2
```powershell
PS> Build-DropboxCache -Path '/Projects'
```

Pre-populates the cache for everything under `/Projects`.

### Example 3
```powershell
PS> Build-DropboxCache -Path '/Projects' -IncludeRevisions
```

Pre-populates the cache under `/Projects` and also caches every file's revision
history.

### Example 4
```powershell
PS> Build-DropboxCache -WhatIf
```

Shows what the command would build without modifying the cache.

### Example 5
```powershell
PS> Build-DropboxCache -Refresh
```

Drains every Dropbox change since the captured cursor into the cache instead of
rebuilding.

### Example 6
```powershell
PS> Build-DropboxCache -Rebuild
```

Wipes the cache and the saved cursor and rebuilds from a freshly captured cursor.

## PARAMETERS

### -DriveName

Name of the Dropbox PSDrive. Defaults to `Dbx`.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: Dbx
Accept pipeline input: False
Accept wildcard characters: False
```

### -IncludeRevisions

After building folder metadata, run a second pass that fetches and caches each
file's revision history. Files fetched recently are skipped so the pass is
resumable.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: False
Accept pipeline input: False
Accept wildcard characters: False
```

### -Path

Dropbox path of the subtree to pre-populate. Defaults to the root `/`.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: 0
Default value: /
Accept pipeline input: False
Accept wildcard characters: False
```

### -Rebuild

Wipe the entire cache (entries and build progress) and the saved delta cursor,
then rebuild from a freshly captured cursor. Cannot be combined with `-Refresh`.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: False
Accept pipeline input: False
Accept wildcard characters: False
```

### -Refresh

Drain account-wide Dropbox changes since the captured delta cursor into the
cache instead of building. This is the incremental refresh path. Cannot be
combined with `-Rebuild`.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: False
Accept pipeline input: False
Accept wildcard characters: False
```

### -ProgressAction

Standard PowerShell common parameter.

```yaml
Type: ActionPreference
Parameter Sets: (All)
Aliases: proga

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Confirm

Prompts you for confirmation before running the command.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases: cf

Required: False
Position: Named
Default value: False
Accept pipeline input: False
Accept wildcard characters: False
```

### -WhatIf

Shows what would happen if the command runs. The command is not run.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases: wi

Required: False
Position: Named
Default value: False
Accept pipeline input: False
Accept wildcard characters: False
```

### CommonParameters

This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable,
-InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable,
-Verbose, -WarningAction, and -WarningVariable. For more information, see
[about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

## OUTPUTS

### System.Management.Automation.PSObject

A build summary with `DriveName`, `Path`, `FoldersCached`, `ItemsFound`,
`FilesWithRevisionsCached`, and `RevisionsCached`. With `-Refresh`, a refresh
summary with `DriveName`, `Mode`, `DeltaPages`, `ItemsAdded`, `ItemsRemoved`,
and `ResetRequired` instead.

## NOTES

## RELATED LINKS

[Update-DropboxCache]()
[Get-DropboxCacheInfo]()
[Clear-DropboxCache]()