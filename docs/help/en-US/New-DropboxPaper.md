---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# New-DropboxPaper

## SYNOPSIS

Creates a new Dropbox Paper document.

## SYNTAX

```
New-DropboxPaper [-Path] <String> [-Content] <String> [-ImportFormat <String>] [-DriveName <String>]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Creates a Paper doc at `-Path` populated from `-Content` (HTML,
Markdown, or plain text). Returns the URL of the new Paper doc.

> **Note**: Dropbox is winding down Paper for new content; this cmdlet
> exposes the existing Paper API and may stop functioning when Dropbox
> retires the endpoints.

## EXAMPLES


### Example 1
```powershell
PS> New-DropboxPaper -Path /Papers/Notes.paper -Content "# Hello`n`nWorld" -ImportFormat markdown
```

Creates a Paper doc from a Markdown string.

### Example 2
```powershell
PS> Get-Content notes.md -Raw | New-DropboxPaper -Path /Papers/Notes.paper
```

Pipes file contents into a new Paper doc using the default Markdown import format.

## PARAMETERS

### -Content

Document body. Accepts pipeline input.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: True
Position: 1
Default value: None
Accept pipeline input: True (ByValue)
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

### -ImportFormat

Source format of ``-Content``: ``html``, ``markdown`` (default), or ``plain_text``.

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: html, markdown, plain_text

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Path

Dropbox path for the new Paper doc (typically ending in ``.paper``).

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
