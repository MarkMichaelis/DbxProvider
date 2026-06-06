---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Get-DropboxCacheInfo

## SYNOPSIS

Returns a snapshot of the in-memory metadata cache for a Dropbox drive.

## SYNTAX

```
Get-DropboxCacheInfo [[-DriveName] <String>] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Emits one summary object (drive name, account id, account email, cache
directory, database path, entry count, and current cache options) followed
by one row per cached folder showing path, item count,
last-validated/last-used timestamps, dirty flag, and a cursor preview.
Useful for verifying that `Get-ChildItem` is hitting the cache.

## EXAMPLES

### Example 1
```powershell
PS> Get-DropboxCacheInfo
```

Shows the cache summary plus per-path entries for the default `Dbx:` drive.

## PARAMETERS

### -DriveName

Name of the Dropbox PSDrive to inspect. Defaults to `Dbx`.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: 0
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

### System.Management.Automation.PSObject

## NOTES

## RELATED LINKS
