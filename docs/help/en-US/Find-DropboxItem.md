---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Find-DropboxItem

## SYNOPSIS

Finds Dropbox files and folders by filename wildcard, reading the local metadata cache with zero API enumeration.

## SYNTAX

```
Find-DropboxItem [[-Name] <String>] [-Path <String>] [-ZeroByteOnly] [-DriveName <String>]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Searches the local Dropbox metadata cache for items whose name matches a
PowerShell `-like` wildcard, emitting each match as a `DropboxItem`. The
enumeration reads straight from the cache database, so it issues no Dropbox
listing calls no matter how large the account is.

Before reading, the cmdlet auto-refreshes the cache from the account delta
cursor (the shared refresh used by every cache-backed cmdlet): it drains the
changes recorded since the last sync, showing a transient progress message and
reporting how many items were added or removed. When the cache has never
captured a delta cursor it captures a baseline and skips the drain; when Dropbox
rejects the saved cursor it warns you to run `Build-DropboxCacheAll.ps1
-Rebuild`.

Populate the cache first with `Build-DropboxCacheAll.ps1` (or `Build-DropboxCache`).
For the server-side indexed search (Dropbox `search_v2`) use `Search-Dropbox`
instead.

## EXAMPLES

### Example 1
```powershell
PS> Find-DropboxItem -Name '*.pdf'
```

Lists every cached PDF in the account.

### Example 2
```powershell
PS> Find-DropboxItem -Name 'budget*' -Path 'Dbx:\Finance'
```

Lists cached items whose name starts with `budget` under the `Finance` subtree.

### Example 3
```powershell
PS> Find-DropboxItem -Name '*' -ZeroByteOnly | Measure-Object
```

Counts every zero-byte file in the cache.

## PARAMETERS

### -Name

Filename `-like` wildcard (`*`, `?`, `[abc]`) matched against each item's name.
Defaults to `*`, which matches every item.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: 0
Default value: *
Accept pipeline input: False
Accept wildcard characters: True
```

### -Path

Dropbox path -- or a drive-qualified path such as `Dbx:\Folder` -- to search
under. Only items at or below this subtree are returned. Defaults to the account
root.

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

### -ZeroByteOnly

Return only zero-byte files. Folders and non-empty files are excluded. By
default items of any size are returned.

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

### -DriveName

Name of the Dropbox PSDrive previously created by `Connect-Dropbox`. Defaults
to `Dbx`.

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
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

## OUTPUTS

### IntelliTect.Dropbox.DropboxItem

## NOTES

Reads only the local metadata cache; it does not contact Dropbox for the
enumeration. Build or refresh the cache with `Build-DropboxCacheAll.ps1`.

## RELATED LINKS