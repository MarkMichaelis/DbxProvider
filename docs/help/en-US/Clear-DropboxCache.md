---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Clear-DropboxCache

## SYNOPSIS

Removes one or all entries from the Dropbox metadata cache.

## SYNTAX

```
Clear-DropboxCache [[-Path] <String>] [-DriveName <String>] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Drops the cached snapshot+cursor for a path (or every path if `-Path`
is omitted) and deletes the corresponding on-disk JSON file. The next
`Get-ChildItem` against the affected path performs a full enumeration.

## EXAMPLES

### Example 1
```powershell
PS> Clear-DropboxCache
```

Drops every cache entry for the default `Dbx:` drive.

### Example 2
```powershell
PS> Clear-DropboxCache -Path '/Photos'
```

Drops just the `/Photos` cache entry.

## PARAMETERS

### -Path

Dropbox path whose cached entry should be cleared. Omit to clear the entire cache.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: 0
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

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

### None

## NOTES

## RELATED LINKS
