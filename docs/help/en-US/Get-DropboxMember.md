---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Get-DropboxMember

## SYNOPSIS

Lists members of a shared folder or shared file.

## SYNTAX

### Folder
```
Get-DropboxMember [-SharedFolderId] <String> [-DriveName <String>] [-ProgressAction <ActionPreference>]
 [<CommonParameters>]
```

### File
```
Get-DropboxMember [-FilePath] <String> [-DriveName <String>] [-ProgressAction <ActionPreference>]
 [<CommonParameters>]
```

## DESCRIPTION

Returns `DropboxMember` objects for everyone with access to the
shared folder (`-SharedFolderId`) or shared file (`-FilePath`).

## EXAMPLES


### Example 1
```powershell
PS> Get-DropboxMember -SharedFolderId "1234567890"
```

Lists members of a shared folder.

### Example 2
```powershell
PS> Get-DropboxMember -FilePath /Specs/api.md | Where-Object AccessLevel -eq editor
```

Filters to editors of a shared file.

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

### -FilePath

Dropbox path of a shared file. Selects the **File** parameter set.

```yaml
Type: String
Parameter Sets: File
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

### -SharedFolderId

Shared-folder ID. Selects the **Folder** parameter set.

```yaml
Type: String
Parameter Sets: Folder
Aliases:

Required: True
Position: 0
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

## OUTPUTS

### IntelliTect.Dropbox.DropboxMember

## NOTES

## RELATED LINKS
