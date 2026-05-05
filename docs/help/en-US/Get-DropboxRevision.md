---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Get-DropboxRevision

## SYNOPSIS

Lists historical revisions of a Dropbox file.

## SYNTAX

```
Get-DropboxRevision [-Path] <String> [-Limit <Int32>] [-DriveName <String>]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Returns up to `-Limit` revisions for a file, newest first, as
`DropboxRevision` objects. Each revision exposes its `Rev` ID,
which is the value passed to `Restore-DropboxRevision` to roll back.

## EXAMPLES


### Example 1
```powershell
PS> Get-DropboxRevision -Path "/notes.txt"
```

Shows the 10 most recent revisions of ``/notes.txt``.

### Example 2
```powershell
PS> Get-DropboxRevision -Path "/notes.txt" -Limit 50 | Format-Table Rev, ServerModified, Size
```

Lists up to 50 revisions in a compact table.

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

### -Limit

Maximum number of revisions to return. Defaults to 10.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Path

Dropbox path of the file whose revisions to list. Accepts pipeline input by value or by the ``FullName`` property.

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

### DbxProvider.Models.DropboxRevision

## NOTES

## RELATED LINKS
