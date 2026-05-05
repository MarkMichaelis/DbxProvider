---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Get-DropboxThumbnail

## SYNOPSIS

Returns an image thumbnail for a Dropbox file.

## SYNTAX

```
Get-DropboxThumbnail [-Path] <String> [-Size <String>] [-Format <String>] [-OutFile <String>]
 [-DriveName <String>] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Generates a thumbnail (JPEG or PNG) at one of Dropbox's supported
sizes. Without `-OutFile` the thumbnail bytes are emitted; with
`-OutFile` the bytes are written to disk.

## EXAMPLES


### Example 1
```powershell
PS> Get-DropboxThumbnail -Path /image.jpg -Size w256h256 -OutFile .\thumb.jpg
```

Saves a 256x256 JPEG thumbnail.

### Example 2
```powershell
PS> Get-DropboxThumbnail /image.jpg -Format png -Size w64h64
```

Emits the raw 64x64 PNG bytes to the pipeline.

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

### -Format

Image format: ``jpeg`` (default) or ``png``.

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: jpeg, png

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -OutFile

Optional local file to write to. Without it the bytes are emitted to the pipeline.

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

Dropbox path of the file.

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

### -Size

Thumbnail size, one of ``w32h32``, ``w64h64``, ``w128h128``, ``w256h256``, ``w480h320``, ``w640h480``, ``w960h640``, ``w1024h768``, ``w2048h1536``. Defaults to ``w64h64``.

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: w32h32, w64h64, w128h128, w256h256, w480h320, w640h480, w960h640, w1024h768, w2048h1536

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
