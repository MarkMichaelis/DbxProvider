---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Search-Dropbox

## SYNOPSIS

Searches for files and folders by name or content in Dropbox.

## SYNTAX

```
Search-Dropbox [-Query] <String> [-Path <String>] [-MaxResults <Int32>] [-IncludeHighlights]
 [-FilenameOnly] [-FileExtensions <String[]>] [-FileCategory <String[]>]
 [-FileStatus <String>] [-OrderBy <String>] [-Wildcard]
 [-DriveName <String>] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Calls the Dropbox `files/search_v2` endpoint with the given query and
returns matching items as `DropboxSearchResult` objects. The search
covers file and folder names; depending on the indexer state Dropbox
may also match file contents for supported types.

**Note:** Dropbox search is *prefix-token-based*, not glob. Filenames are
split into tokens on punctuation and whitespace, and each query token is
matched as a prefix against a filename token. `*` and `?` in `-Query`
are treated as literal characters and effectively ignored. Use
`-FileExtensions` for true extension filtering, or `-Wildcard` to apply
PowerShell wildcard semantics on top of the search results.

Use `-Path` to restrict the search to a subtree, `-MaxResults` to
cap the page size, and `-IncludeHighlights` to receive snippet
highlights in the result objects.

## EXAMPLES


### Example 1
```powershell
PS> Search-Dropbox -Query "budget"
```

Returns up to 100 items whose name or contents match "budget" anywhere in the account.

### Example 2
```powershell
PS> Search-Dropbox -Query "*.docx" -Path "/Reports" -MaxResults 25
```

Restricts the search to ``/Reports`` and limits the result set to 25 items.

### Example 3
```powershell
PS> Search-Dropbox -Query "TODO" -IncludeHighlights | Select-Object Path, Highlights
```

Returns matches with content snippets so you can see why each item matched.

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

Search expression. Supports plain words and simple wildcards (e.g. ``*.docx``).

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: True
Position: 0
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -FilenameOnly

Restrict matching to filenames; skip file content indexing. Faster and
avoids false positives from document contents. Does NOT enable
wildcards (Dropbox search is prefix-token-based; use `-Wildcard` for
PowerShell wildcard semantics).

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

### -FileExtensions

Server-side filter on file extensions (e.g. `pdf`, `docx`). This is the
correct way to do an `*.docx`-style filter — `-Query "*.docx"` will not
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

### -Wildcard

Treat `-Query` as a PowerShell wildcard pattern (`*`, `?`, `[abc]`).
Implies `-FilenameOnly`. Tokens are derived from the pattern for the
server-side query, then results are post-filtered with
`WildcardPattern` to enforce true PowerShell glob semantics.

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

### DbxProvider.Models.DropboxSearchResult

## NOTES

## RELATED LINKS
