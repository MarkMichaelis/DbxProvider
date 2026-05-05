---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Get-DropboxTemporaryLink

## SYNOPSIS

Returns a short-lived direct download URL for a Dropbox file.

## SYNTAX

```
Get-DropboxTemporaryLink [-Path] <String> [-DriveName <String>] [-ProgressAction <ActionPreference>]
 [<CommonParameters>]
```

## DESCRIPTION

Generates a temporary (typically 4-hour) direct-download URL for the
given Dropbox file. Unlike a shared link, this URL streams the raw
file contents and is intended for programmatic download by systems
that cannot authenticate against the Dropbox API directly.

## EXAMPLES


### Example 1
```powershell
PS> Get-DropboxTemporaryLink -Path /report.pdf
```

Returns a direct-download URL good for ~4 hours.

### Example 2
```powershell
PS> Invoke-WebRequest (Get-DropboxTemporaryLink /report.pdf) -OutFile .\report.pdf
```

Downloads via the temporary link using a generic HTTP client.

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

Dropbox path of the file. Accepts pipeline input by value or by ``FullName`` property.

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

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### System.String

## OUTPUTS

### System.String

## NOTES

## RELATED LINKS
