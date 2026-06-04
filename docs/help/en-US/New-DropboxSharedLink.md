---
external help file: DbxProvider.dll-Help.xml
Module Name: DbxProvider
online version:
schema: 2.0.0
---

# New-DropboxSharedLink

## SYNOPSIS

Creates a shared link for a Dropbox file or folder.

## SYNTAX

```
New-DropboxSharedLink [-Path] <String> [-Visibility <String>] [-Expires <DateTime>] [-DriveName <String>]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Generates a Dropbox shared link with optional visibility and
expiration. Returns a `DropboxSharedLink` object whose `Url`
property is the link to share.

## EXAMPLES


### Example 1
```powershell
PS> New-DropboxSharedLink -Path /report.pdf
```

Creates a shared link with default (account-policy) visibility and no expiration.

### Example 2
```powershell
PS> New-DropboxSharedLink /draft.docx -Visibility team_only -Expires (Get-Date).AddDays(7)
```

Creates a team-only link that expires in seven days.

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

### -Expires

UTC date/time at which the link should expire (Dropbox Professional / Business only).

```yaml
Type: DateTime
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Path

Dropbox path of the item to share. Accepts pipeline input by value or by ``FullName`` property.

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

### -Visibility

Link visibility: ``public``, ``team_only``, or ``password``. When omitted, the account default policy applies.

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: public, team_only, password

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

### IntelliTect.Dropbox.DropboxSharedLink

## NOTES

## RELATED LINKS
