---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Invoke-DropboxUpload

## SYNOPSIS

Uploads a local file to Dropbox, with automatic large-file chunking.

## SYNTAX

```
Invoke-DropboxUpload [-Source] <String> [-DropboxPath] <String> [-Force] [-WriteMode <String>]
 [-DriveName <String>] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Uploads `-Source` to `-DropboxPath`. Files larger than the
single-shot upload limit are automatically split into upload-session
chunks. Use `-WriteMode` to control how an existing remote file is
treated (`add`, `overwrite`, `update`). Returns the resulting
`DropboxItem`.

## EXAMPLES


### Example 1
```powershell
PS> Invoke-DropboxUpload -Source .\report.docx -DropboxPath /Reports/report.docx
```

Uploads a local file, overwriting any existing remote file with the same name.

### Example 2
```powershell
PS> Invoke-DropboxUpload .\photo.jpg /Photos/photo.jpg -WriteMode add
```

Uploads only if no file already exists at the destination (Dropbox auto-renames on conflict).

### Example 3
```powershell
PS> Get-ChildItem .\backup -File | ForEach-Object { Invoke-DropboxUpload $_.FullName "/Backup/$($_.Name)" }
```

Bulk-uploads every file in a local folder.

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

### -DropboxPath

Destination path inside Dropbox (e.g. ``/Reports/report.docx``).

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

### -Force

Reserved for future use; currently has no effect (overwrite behavior is controlled by ``-WriteMode``).

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

### -Source

Local path of the file to upload. Relative paths are resolved against the current PowerShell location.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: True
Position: 0
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -WriteMode

How to handle an existing remote file: ``add`` (auto-rename on conflict), ``overwrite`` (default), or ``update`` (require previous rev match).

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: add, overwrite, update

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

## RELATED LINKS
