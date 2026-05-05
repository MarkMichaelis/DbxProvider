---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# Add-DropboxMember

## SYNOPSIS

Adds a member (by email) to a shared folder or shared file.

## SYNTAX

### Folder
```
Add-DropboxMember [-SharedFolderId] <String> [-Email] <String> [-AccessLevel <String>] [-DriveName <String>]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### File
```
Add-DropboxMember [-FilePath] <String> [-Email] <String> [-AccessLevel <String>] [-DriveName <String>]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Invites the user identified by `-Email` to a shared folder
(`-SharedFolderId`) or a shared file (`-FilePath`) at the
specified `-AccessLevel`.

## EXAMPLES


### Example 1
```powershell
PS> Add-DropboxMember -SharedFolderId "1234567890" -Email alice@contoso.com -AccessLevel editor
```

Invites Alice as an editor on a shared folder.

### Example 2
```powershell
PS> Add-DropboxMember -FilePath /Specs/api.md -Email bob@contoso.com -AccessLevel viewer
```

Invites Bob to view a specific file.

## PARAMETERS

### -AccessLevel

Permission to grant: ``editor``, ``viewer``, or ``viewer_no_comment``. Defaults to ``viewer``.

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: editor, viewer, viewer_no_comment

Required: False
Position: Named
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

### -Email

Email address of the user to invite.

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

### -FilePath

Dropbox path of a file to share. Selects the **File** parameter set.

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

### System.Object
## NOTES

## RELATED LINKS
