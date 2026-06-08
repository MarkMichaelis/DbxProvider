---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Search-Dropbox

## SYNOPSIS

Searches Dropbox for files and folders by name (cache-first).

## SYNTAX

```
Search-Dropbox [-Query] <String> [-Path <String>] [-NoCache] [-ZeroByteOnly] [-MaxResults <Int32>]
 [-IncludeHighlights] [-FilenameOnly] [-FileExtensions <String[]>] [-FileCategory <String[]>]
 [-FileStatus <String>] [-OrderBy <String>] [-DriveName <String>] [-ProgressAction <ActionPreference>]
 [<CommonParameters>]
```

## DESCRIPTION

By default `Search-Dropbox` searches the local metadata cache: it issues zero
Dropbox API calls for the lookup, is exhaustive, and auto-refreshes from the
account delta cursor first, which is far faster than crawling the account. The
query is matched as a glob when it contains a wildcard (`*`, `?` or `[`) and as
a substring otherwise. Cache matches are returned as `DropboxItem` objects.

Use `-NoCache` to fall back to the Dropbox `files/search_v2` index instead. The
server search also matches file *contents* for supported types and honors the
server-side filters (`-FileCategory`, `-FileExtensions`, `-FileStatus`,
`-OrderBy`, `-IncludeHighlights`); it returns `DropboxSearchResult` objects.

Build or refresh the cache with `Build-DropboxCacheAll.ps1`. Use `-Path` to
restrict either engine to a subtree.

## EXAMPLES

### Example 1
```powershell
PS> Search-Dropbox "budget"
```

Substring search of the local cache; returns every cached item whose name contains "budget".

### Example 2
```powershell
PS> Search-Dropbox "*.docx" -Path "/Reports"
```

Wildcard (glob) search of the cache under ``/Reports`` - the wildcard is auto-detected, no switch needed.

### Example 3
```powershell
PS> Search-Dropbox "TODO" -NoCache -IncludeHighlights | Select-Object Path, Highlights
```

Server-side ``search_v2`` query (matches file contents) with snippet highlights so you can see why each item matched.

### Example 4
```powershell
PS> Search-Dropbox "*" -ZeroByteOnly | Measure-Object
```

Counts every zero-byte file in the cache.

## PARAMETERS

### -DriveName

Name of the Dropbox PSDrive previously created by `Connect-Dropbox`.
Defaults to `Dbx`. Specify a different name when you have connected
to multiple Dropbox accounts in the same session.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -FileCategory

Server-side filter on Dropbox file categories. Valid values:
`Image`, `Document`, `Pdf`, `Spreadsheet`, `Presentation`, `Audio`,
`Video`, `Folder`, `Paper`, `Others`.

```yaml
Type: String[]
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -FileExtensions

Server-side filter on file extensions (e.g. `pdf`, `docx`). This is the
correct way to do an `*.docx`-style filter - `-Query "*.docx"` will not
work because Dropbox treats `*` as a literal token character.

```yaml
Type: String[]
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -FilenameOnly

Restrict matching to filenames; skip file content indexing. Faster and
avoids false positives from document contents. Server mode only
(`-NoCache`); ignored in the default cache mode, which always matches on
names.

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

### -FileStatus

Search active or deleted files. Valid values: `Active`, `Deleted`.
Defaults to `Active`.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: Active
Accept pipeline input: False
Accept wildcard characters: False
```

### -IncludeHighlights

Include match highlights / snippets on each result.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -MaxResults

Maximum number of results to return. Defaults to 100.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -OrderBy

Result ordering. Valid values: `Relevance`, `LastModifiedTime`. Defaults
to `Relevance`.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: Relevance
Accept pipeline input: False
Accept wildcard characters: False
```

### -Path

Restrict the search to this Dropbox folder (e.g. ``/Reports``). Empty (the default) searches the entire account.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -ProgressAction

Standard PowerShell common parameter that controls how progress records
are reported. See [about_CommonParameters](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_commonparameters).

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

### -Query

Search query. A query containing a wildcard (`*`, `?` or `[`) is matched
as a glob; otherwise it is a substring match. In cache mode this matches
item names; with `-NoCache` it is passed to the server index.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: True
Position: 0
Default value: None
Accept pipeline input: False
Accept wildcard characters: True
```

### -NoCache

Search the server-side `search_v2` index instead of the local metadata
cache. Slower, but matches file contents and honors the server-side
filters (`-FileCategory`, `-FileExtensions`, `-FileStatus`, `-OrderBy`,
`-IncludeHighlights`). Returns `DropboxSearchResult` objects.

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

### -ZeroByteOnly

Match only zero-byte files (skips folders and non-empty files). Cache
mode only; ignored with `-NoCache`.

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

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

## OUTPUTS

### IntelliTect.Dropbox.DropboxItem

Returned in the default cache mode (one per matching cached item).

### IntelliTect.Dropbox.DropboxSearchResult

Returned with `-NoCache` (one per server search hit).

## NOTES

## RELATED LINKS
