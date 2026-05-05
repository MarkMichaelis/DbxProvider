---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Set-DropboxPaper

## SYNOPSIS

Updates an existing Dropbox Paper document.

## SYNTAX

```
Set-DropboxPaper [-Path] <String> [-Content] <String> [-ImportFormat <String>] [-UpdatePolicy <String>]
 [-DriveName <String>] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Modifies the Paper doc at `-Path` using the supplied `-Content`
and `-UpdatePolicy` (`overwrite`, `prepend`, or `append`).

## EXAMPLES


### Example 1
```powershell
PS> Set-DropboxPaper -Path /Papers/Notes.paper -Content "## Update" -UpdatePolicy append
```

Appends a new section to an existing Paper doc.

### Example 2
```powershell
PS> Get-Content fresh.md -Raw | Set-DropboxPaper -Path /Papers/Notes.paper -UpdatePolicy overwrite
```

Overwrites the doc with new Markdown contents.

## PARAMETERS

### -Content

New document body. Accepts pipeline input.

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

Dropbox path of the existing Paper doc.

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

### -UpdatePolicy

How to apply the new content: ``overwrite`` (default), ``prepend``, or ``append``.

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: overwrite, prepend, append

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
