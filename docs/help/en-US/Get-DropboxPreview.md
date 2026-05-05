---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Get-DropboxPreview

## SYNOPSIS

Returns a PDF preview of a Dropbox file.

## SYNTAX

```
Get-DropboxPreview [-Path] <String> [-OutFile <String>] [-DriveName <String>]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Downloads a PDF preview rendering for documents Dropbox knows how to
preview (Office files, RTF, plain text, etc.). Without `-OutFile`
the preview bytes are emitted to the pipeline; with `-OutFile` the
bytes are written to disk and a `FileInfo` is returned.

## EXAMPLES


### Example 1
```powershell
PS> Get-DropboxPreview -Path /report.docx -OutFile .\preview.pdf
```

Saves a PDF preview of a Word document to disk.

### Example 2
```powershell
PS> $bytes = Get-DropboxPreview /report.docx; $bytes.Length
```

Returns preview bytes directly.

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

### -OutFile

Optional local path to write the preview to. Without this parameter the bytes are emitted to the pipeline.

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

Dropbox path of the file to preview.

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

### System.Byte[]

### System.IO.FileInfo

## NOTES

## RELATED LINKS
