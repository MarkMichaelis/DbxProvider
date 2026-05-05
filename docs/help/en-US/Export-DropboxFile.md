---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Export-DropboxFile

## SYNOPSIS

Exports a Dropbox file (e.g. Google Docs, Sheets) to a downloadable format.

## SYNTAX

```
Export-DropboxFile [-Path] <String> [-OutFile <String>] [-DriveName <String>]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Some Dropbox-stored files (Google Docs, Sheets, Slides, Paper) cannot
be downloaded as-is and must be **exported** to a portable format
(PDF, DOCX, XLSX, etc.). This cmdlet performs that export. Without
`-OutFile` the bytes are emitted to the pipeline; with `-OutFile`
the bytes are written and a `FileInfo` is returned.

## EXAMPLES


### Example 1
```powershell
PS> Export-DropboxFile -Path /Drafts/Plan.gdoc -OutFile .\plan.docx
```

Exports a Google Doc as a Word document.

### Example 2
```powershell
PS> $bytes = Export-DropboxFile /Sheets/Budget.gsheet
```

Captures the exported bytes (XLSX) without writing to disk.

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

Optional local file to write to. Without it, raw bytes are emitted.

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

Dropbox path of the file to export.

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
