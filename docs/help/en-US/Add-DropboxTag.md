---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Add-DropboxTag

## SYNOPSIS

Adds a user-defined tag to a Dropbox file or folder.

## SYNTAX

```
Add-DropboxTag [-Path] <String> [-Tag] <String> [-DriveName <String>] [-ProgressAction <ActionPreference>]
 [<CommonParameters>]
```

## DESCRIPTION

Attaches a Dropbox **tag** (a string label) to the item at `-Path`.
Tags are visible in the Dropbox web UI and queryable via the API. Tag
names follow Dropbox's rules (lower-case letters, digits, and
underscores; max 32 characters).

## EXAMPLES


### Example 1
```powershell
PS> Add-DropboxTag -Path /report.pdf -Tag final
```

Tags ``/report.pdf`` with ``final``.

### Example 2
```powershell
PS> Get-ChildItem Dbx:\Reports\*.pdf | Add-DropboxTag -Tag archived
```

Tags every PDF in ``/Reports`` as ``archived``.

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

### -Path

Dropbox path to tag. Accepts pipeline input.

```yaml
Type: String
Parameter Sets: (All)
Aliases: FullName

Required: True
Position: 0
Default value: None
Accept pipeline input: True (ByPropertyName, ByValue)
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

### -Tag

Tag name to add. Lower-case letters, digits, underscores; max 32 characters.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: True
Position: 1
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### System.String

## OUTPUTS

### System.Object
## NOTES

## RELATED LINKS
