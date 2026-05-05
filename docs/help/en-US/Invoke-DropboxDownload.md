---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Invoke-DropboxDownload

## SYNOPSIS

Downloads a file from Dropbox to local disk.

## SYNTAX

```
Invoke-DropboxDownload [-Path] <String> [-Destination] <String> [-Force] [-DriveName <String>]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Streams the file at the given Dropbox path to a local destination.
Refuses to overwrite an existing local file unless `-Force` is
specified. Creates the destination directory if missing. Returns the
resulting `System.IO.FileInfo`.

## EXAMPLES


### Example 1
```powershell
PS> Invoke-DropboxDownload -Path /report.pdf -Destination C:\Temp\report.pdf
```

Downloads ``/report.pdf`` to the local path.

### Example 2
```powershell
PS> Invoke-DropboxDownload /image.png .\image.png -Force
```

Overwrites an existing local file using positional arguments.

### Example 3
```powershell
PS> Get-ChildItem Dbx:\Reports\*.pdf | Invoke-DropboxDownload -Destination .\reports\
```

Pipes Dropbox items into the cmdlet via the ``FullName`` alias to bulk-download a folder.

## PARAMETERS

### -Destination

Local file or directory path to write to. Relative paths are resolved against the current PowerShell location.

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

### -Force

Overwrite the destination file if it already exists.

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

### -Path

Dropbox path of the file to download. Accepts pipeline input by value or by ``FullName`` property.

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

### System.IO.FileInfo

## NOTES

## RELATED LINKS
