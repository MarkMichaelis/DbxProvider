---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Build-DropboxCache

## SYNOPSIS

Pre-populates the metadata cache for a subtree using a single recursive
`/files/list_folder` call.

## SYNTAX

```
Build-DropboxCache [[-Path] <String>] [-DriveName <String>] [-ProgressAction <ActionPreference>]
 [<CommonParameters>]
```

## DESCRIPTION

Walks the Dropbox tree under `-Path` with one recursive `list_folder`, groups
the flat result by parent folder, and stores each group as a per-folder cache
entry. This is far cheaper than the lazy, one-`list_folder`-per-folder warming
that happens on demand during `Get-ChildItem`.

Because a recursive listing returns only a single subtree cursor (not a cursor
per folder), the entries created by this command start without a per-folder
cursor. Each built entry acquires its real per-folder cursor automatically on
its first validated read (the first `Get-ChildItem` or `Update-DropboxCache`
for that folder), after which it behaves like any other cursor-validated entry.
Dropbox always remains the master: a built entry is never served without first
reconciling against the server.

If the cache is disabled (`Set-DropboxCacheOption -Disable`), the command warns
and does nothing.

The command emits a summary object reporting the number of folders cached and
the total number of items found.

This command populates folder/metadata listings only. Fetching file revision
history is a separate feature and is not performed here.

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

### CommonParameters

This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable,
-InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable,
-Verbose, -WarningAction, and -WarningVariable. For more information, see
[about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

## OUTPUTS

### System.Management.Automation.PSObject

A summary with `DriveName`, `Path`, `FoldersCached`, and `ItemsFound`.

## NOTES

## RELATED LINKS

[Update-DropboxCache]()
[Get-DropboxCacheInfo]()
[Clear-DropboxCache]()