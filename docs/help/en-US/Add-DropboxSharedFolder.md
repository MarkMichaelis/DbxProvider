---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Add-DropboxSharedFolder

## SYNOPSIS

Converts a Dropbox folder into a shared folder.

## SYNTAX

```
Add-DropboxSharedFolder [-Path] <String> [-DriveName <String>] [-ProgressAction <ActionPreference>]
 [<CommonParameters>]
```

## DESCRIPTION

Begins sharing the folder at `-Path` and returns the resulting
shared-folder ID. Use this ID with `Add-DropboxMember`,
`Get-DropboxMember`, `Remove-DropboxMember`, and
`Remove-DropboxSharedFolder`.

## EXAMPLES


### Example 1
```powershell
PS> Add-DropboxSharedFolder -Path /Project
```

Shares ``/Project``; the returned ID is then used to add members.

### Example 2
```powershell
PS> $id = Add-DropboxSharedFolder /Project; Add-DropboxMember -SharedFolderId $id -Email alice@contoso.com -AccessLevel editor
```

Shares a folder and immediately invites a member as editor.

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

Dropbox path of the folder to share.

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

### None

## OUTPUTS

### System.String

## NOTES

## RELATED LINKS
