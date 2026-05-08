---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Update-DropboxCache

## SYNOPSIS

Eagerly validates one or all cache entries by calling
`/files/list_folder/continue` and applying any deltas.

## SYNTAX

```
Update-DropboxCache [[-Path] <String>] [-DriveName <String>] [-ProgressAction <ActionPreference>]
 [<CommonParameters>]
```

## DESCRIPTION

Forces the validate-and-merge step that normally runs on the first
`Get-ChildItem` for a path. Useful for pre-warming a script's working
set or for verifying that an external change has propagated.

## EXAMPLES

### Example 1
```powershell
PS> Update-DropboxCache -Path '/'
```

Applies any pending deltas to the root folder's cache entry.

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

Dropbox path whose cache should be refreshed. Omit to refresh every cached entry.

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
